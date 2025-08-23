using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;

public class PointCloudRenderer : MonoBehaviour
{
    [Header("Data Source")]
    public ZmqFrameReceiver receiver;

    [Header("Rendering")]
    public Material instancedMaterial;        // uses PointCloud/BillboardURP
    public ComputeShader pointCloudCompute;   // CubeRendering.compute
    public float pointSizeWorld = 0.01f;
    public float scale = 100.0f;

    [Header("Culling (updated by incoming packets)")]
    public float cullMin = 0.01f;
    public float cullMax = 10.0f;
    public float xCull = 2.0f;
    public float yCull = 2.0f;

    [Header("Controller Controls")]
    public float moveSpeedPerSecond = 0.2f;
    public float rotationSpeedDegreesPerSecond = 25.0f;
    public float pointSizeAdjustPerSecond = 0.01f;

    [Header("Diagnostics")]
    public bool logPerformance = true;

    // --- buffers & state ---
    private Texture2D rgbTexture;
    private ComputeBuffer depthBuffer;      // uint per pixel (depth)
    private uint[] depth;
    private ComputeBuffer matrixBuffer;     // per-instance TRS matrices
    private ComputeBuffer argsBuffer;       // indirect draw args

    private ComputeBuffer validCountBuffer;     // GPU counter for valid
    private ComputeBuffer visibleCountBuffer;   // GPU counter for visible
    private readonly uint[] validCountCPU   = new uint[1];
    private readonly uint[] visibleCountCPU = new uint[1];

    private int validPoints   = 0;
    private int visiblePoints = 0;
    private float lastCountSample = 0f;

    private Mesh instanceMesh;              // a Quad
    private Bounds renderBounds;

    private int width = 1, height = 1;
    private float fx = 591.4252f, fy = 591.4252f, cx = 320.1326f, cy = 239.1477f;

    private Vector3 renderingLocation = Vector3.zero;
    private Quaternion renderingRotation = Quaternion.identity;
    private Matrix4x4 poseMatrix = Matrix4x4.identity;

    // FPS counters
    private int renderFrameCounter = 0;
    private int streamFrameCounter = 0;
    private float fpsWindowStart = 0f;

    // Exposed for HUD
    public int ValidPoints => validPoints;
    public int VisiblePoints => visiblePoints;
    public int FrameWidth => width;
    public int FrameHeight => height;
    public float LastRenderFps { get; private set; }
    public float LastStreamFps { get; private set; }
    public float ValidDensity01   => (width * height > 0) ? (float)validPoints / (width * height)   : 0f;
    public float VisibleDensity01 => (width * height > 0) ? (float)visiblePoints / (width * height) : 0f;

    void Start()
    {
        // use a Quad mesh (2 tris) for billboarded points
        var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        instanceMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(temp);

        renderBounds = new Bounds(Vector3.zero, Vector3.one * 1000);

        fpsWindowStart = Time.unscaledTime;
        InitBuffers();
    }

    private void InitBuffers()
    {
        matrixBuffer?.Release();
        argsBuffer?.Release();
        depthBuffer?.Release();
        validCountBuffer?.Release();
        visibleCountBuffer?.Release();

        rgbTexture = new Texture2D(Mathf.Max(1, width), Mathf.Max(1, height), TextureFormat.RGB24, false);

        depthBuffer = new ComputeBuffer(width * height, sizeof(uint));
        depth = new uint[width * height];

        matrixBuffer = new ComputeBuffer(width * height, Marshal.SizeOf(typeof(Matrix4x4)));
        instancedMaterial.SetBuffer("matrixBuffer", matrixBuffer);

        instancedMaterial.SetTexture("_ColorTex", rgbTexture);
        instancedMaterial.SetInt("_Width", width);
        instancedMaterial.SetInt("_Height", height);

        validCountBuffer   = new ComputeBuffer(1, sizeof(uint));
        visibleCountBuffer = new ComputeBuffer(1, sizeof(uint));

        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        uint[] args = new uint[5] {
            (instanceMesh != null) ? instanceMesh.GetIndexCount(0) : 0, // index count
            (uint)(width * height),                                     // instance count
            (instanceMesh != null) ? instanceMesh.GetIndexStart(0) : 0,
            (instanceMesh != null) ? instanceMesh.GetBaseVertex(0) : 0,
            0
        };
        argsBuffer.SetData(args);

        Debug.Log($"[Renderer] Init buffers with size {width}x{height}");
    }

    void Update()
    {
        HandleControllers();

        // update batch bounds center to avoid whole-batch culling
        renderBounds.center = renderingLocation;

        // counts Unity render loop FPS
        renderFrameCounter++;

        // Pull latest frame from receiver (if any)
        if (receiver != null && receiver.TryGetLatest(out var pkt) && pkt.IsValid)
        {
            bool sizeChanged = (pkt.width != width) || (pkt.height != height);
            width = pkt.width;
            height = pkt.height;

            fx = pkt.fx; fy = pkt.fy; cx = pkt.cx; cy = pkt.cy;
            cullMin = pkt.cullMin; cullMax = pkt.cullMax; xCull = pkt.xCull; yCull = pkt.yCull;

            if (sizeChanged || rgbTexture == null || rgbTexture.width != width || rgbTexture.height != height)
                InitBuffers();

            // RGB (compressed)
            rgbTexture.LoadImage(pkt.rgbBytes);

            // Depth (ushort -> uint buffer)
            SetDepthBuffer(pkt.depthBytes);

            // Compute + fill matrixBuffer (positions)
            DispatchComputeShader();

            // Count only frames actually applied
            streamFrameCounter++;
        }

        // Update per-frame material params (camera vectors & point size)
        var cam = Camera.main;
        if (cam != null)
        {
            instancedMaterial.SetVector("_CamRight", cam.transform.right);
            instancedMaterial.SetVector("_CamUp",    cam.transform.up);
        }
        instancedMaterial.SetFloat("_PointSizeWorld", pointSizeWorld);

        // Draw instanced billboards
        if (argsBuffer != null && instancedMaterial != null)
        {
            Graphics.DrawMeshInstancedIndirect(
                instanceMesh, 0, instancedMaterial, renderBounds, argsBuffer);
        }

        if (logPerformance) LogFps();
    }

    private void SetDepthBuffer(byte[] latestDepthBytes)
    {
        int count = latestDepthBytes.Length / 2;
        if (depth == null || depth.Length < count) depth = new uint[count];

        var depthUshorts = new ushort[count];
        Buffer.BlockCopy(latestDepthBytes, 0, depthUshorts, 0, latestDepthBytes.Length);
        for (int i = 0; i < count; i++) depth[i] = depthUshorts[i];

        depthBuffer.SetData(depth);
    }

    private void DispatchComputeShader()
    {
        int kernel = pointCloudCompute.FindKernel("CSMain");

        // zero counters before dispatch
        validCountCPU[0] = 0;
        visibleCountCPU[0] = 0;
        validCountBuffer.SetData(validCountCPU);
        visibleCountBuffer.SetData(visibleCountCPU);

        // bind buffers
        pointCloudCompute.SetBuffer(kernel, "depthBuffer", depthBuffer);
        pointCloudCompute.SetBuffer(kernel, "matrixBuffer", matrixBuffer);
        pointCloudCompute.SetBuffer(kernel, "_ValidCount", validCountBuffer);
        pointCloudCompute.SetBuffer(kernel, "_VisibleCount", visibleCountBuffer);

        // uniforms
        pointCloudCompute.SetInt("_Width", width);
        pointCloudCompute.SetInt("_Height", height);
        pointCloudCompute.SetFloat("_Fx", fx);
        pointCloudCompute.SetFloat("_Fy", fy);
        pointCloudCompute.SetFloat("_Cx", cx);
        pointCloudCompute.SetFloat("_Cy", cy);
        pointCloudCompute.SetFloat("_Scale", scale);
        pointCloudCompute.SetFloat("_CubeSize", 1.0f); // unused by billboard shader
        pointCloudCompute.SetFloat("_CullMinZ", cullMin);
        pointCloudCompute.SetFloat("_CullMaxZ", cullMax);
        pointCloudCompute.SetFloat("_CullX", xCull);
        pointCloudCompute.SetFloat("_CullY", yCull);

        poseMatrix = Matrix4x4.TRS(renderingLocation, renderingRotation, Vector3.one);
        pointCloudCompute.SetMatrix("_PoseMatrix", poseMatrix);

        // camera VP for frustum test
        var cam = Camera.main;
        if (cam != null)
        {
            Matrix4x4 VP = cam.projectionMatrix * cam.worldToCameraMatrix;
            pointCloudCompute.SetMatrix("_VP", VP);
        }

        int tgx = Mathf.CeilToInt(width / 8.0f);
        int tgy = Mathf.CeilToInt(height / 8.0f);
        pointCloudCompute.Dispatch(kernel, tgx, tgy, 1);

        // read back ~2x/sec to avoid stalls
        if (Time.unscaledTime - lastCountSample > 0.5f)
        {
            validCountBuffer.GetData(validCountCPU);     // 4 bytes
            visibleCountBuffer.GetData(visibleCountCPU); // 4 bytes
            validPoints   = (int)validCountCPU[0];
            visiblePoints = (int)visibleCountCPU[0];
            lastCountSample = Time.unscaledTime;
        }
    }

    private void HandleControllers()
    {
        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        var left  = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        float move = moveSpeedPerSecond * Time.deltaTime;
        float rotDeg = rotationSpeedDegreesPerSecond * Time.deltaTime;
        float sizeDelta = pointSizeAdjustPerSecond * Time.deltaTime;

        // Right stick: yaw/pitch
        if (right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rightStick))
        {
            float yaw = rightStick.x * rotDeg;
            float pitch = -rightStick.y * rotDeg;
            renderingRotation = renderingRotation * Quaternion.Euler(pitch, yaw, 0f);
        }

        // Right buttons: roll
        if (right.TryGetFeatureValue(CommonUsages.primaryButton, out bool aPressed) && aPressed)
            renderingRotation = renderingRotation * Quaternion.Euler(0f, 0f, -rotDeg);
        if (right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool bPressed) && bPressed)
            renderingRotation = renderingRotation * Quaternion.Euler(0f, 0f,  rotDeg);

        // Left stick: translate X/Z
        if (left.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 leftStick))
            renderingLocation += new Vector3(leftStick.x, 0f, leftStick.y) * move;

        // Left trigger/grip: Y up/down
        if (left.TryGetFeatureValue(CommonUsages.trigger, out float lTrig) && lTrig > 0.1f)
            renderingLocation += Vector3.up * move * lTrig;
        if (left.TryGetFeatureValue(CommonUsages.grip, out float lGrip) && lGrip > 0.1f)
            renderingLocation += Vector3.down * move * lGrip;

        // Left buttons: adjust point size
        if (left.TryGetFeatureValue(CommonUsages.primaryButton, out bool xPressed) && xPressed)
            pointSizeWorld = Mathf.Max(0.0001f, pointSizeWorld - sizeDelta);
        if (left.TryGetFeatureValue(CommonUsages.secondaryButton, out bool yPressed) && yPressed)
            pointSizeWorld += sizeDelta;
    }

    private void LogFps()
    {
        float now = Time.unscaledTime;
        if (now - fpsWindowStart >= 1.0f)
        {
            LastRenderFps = renderFrameCounter;
            LastStreamFps = streamFrameCounter;

            Debug.Log($"[Renderer] Render FPS: {renderFrameCounter}, Stream FPS: {streamFrameCounter}, " +
                      $"Size: {width}x{height}, Valid: {validPoints} ({ValidDensity01 * 100f:F1}%), " +
                      $"Visible: {visiblePoints} ({VisibleDensity01 * 100f:F1}%), PtSize: {pointSizeWorld:F4}");

            renderFrameCounter = 0;
            streamFrameCounter = 0;
            fpsWindowStart = now;
        }
    }

    void OnDestroy()
    {
        matrixBuffer?.Release();
        argsBuffer?.Release();
        depthBuffer?.Release();
        validCountBuffer?.Release();
        visibleCountBuffer?.Release();
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus) InitBuffers();
    }

    void OnApplicationPause(bool pause)
    {
        if (!pause) InitBuffers();
    }
}
