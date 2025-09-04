using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.VFX;


public class PointCloudRenderer : MonoBehaviour
{
    [Header("Data Source")]
    public ZmqFrameReceiver receiver;

    [Header("Compute")]
    public ComputeShader pointCloudCompute;   // CubeRendering.compute
    [Tooltip("Visual/world scale. Keep 1.0 while debugging; raise later if you really want.")]
    public float scale = 1.0f;

    [Header("VFX")]
    public VisualEffect vfx;                  // Assign the VisualEffect component
    [Tooltip("Must be <= VFX Graph Capacity. We'll clamp Count to this.")]
    public int vfxCapacity = 500_000;

    [Header("Point Controls")]
    public float pointSizeWorld = 0.01f;

    [Header("Culling (updated by incoming packets)")]
    [Tooltip("Meters")]
    public float cullMin = 0.01f;
    [Tooltip("Meters")]
    public float cullMax = 10.0f;
    public float xCull = 2.0f;
    public float yCull = 2.0f;

    [Header("Controller Controls")]
    public float moveSpeedPerSecond = 0.2f;
    public float rotationSpeedDegreesPerSecond = 25.0f;
    public float pointSizeAdjustPerSecond = 0.01f;

    [Header("Diagnostics")]
    public bool logPerformance = true;

    // ---------- GPU resources ----------
    private Texture2D rgbTexture;
    private ComputeBuffer depthBuffer;          // uint per pixel (depth)
    private uint[] depthCPU;

    // Use GraphicsBuffer so we can bind to both ComputeShader and VFX
    private GraphicsBuffer positionsBuffer;     // float3 per point
    private GraphicsBuffer colorsBuffer;        // float4 per point (optional)
    private int bufferCapacity = 0;             // width*height allocated

    private ComputeBuffer validCountBuffer;     // 1 uint
    private ComputeBuffer visibleCountBuffer;   // 1 uint
    private readonly uint[] validCountCPU   = new uint[1];
    private readonly uint[] visibleCountCPU = new uint[1];

    private int validPoints   = 0;
    private int visiblePoints = 0;
    private float lastCountSample = 0f;

    // Stream state
    private int width = 1, height = 1;
    private float fx = 591.4252f, fy = 591.4252f, cx = 320.1326f, cy = 239.1477f;

    // Pose
    private Vector3 renderingLocation = Vector3.zero;
    private Quaternion renderingRotation = Quaternion.identity;
    private Matrix4x4 poseMatrix = Matrix4x4.identity;

    // FPS diagnostics
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

    // Kernel cache
    private int csKernel = -1;

    // VFX property IDs
    static readonly int ID_PointSizeWorld = Shader.PropertyToID("PointSizeWorld");
    static readonly int ID_Count          = Shader.PropertyToID("Count");
    static readonly int ID_Positions      = Shader.PropertyToID("Positions");
    static readonly int ID_Colors         = Shader.PropertyToID("Colors");

    // ---- Lifecycle ----
    void Start()
    {
        fpsWindowStart = Time.unscaledTime;
        csKernel = pointCloudCompute != null ? pointCloudCompute.FindKernel("CSMain") : -1;
        InitBuffers();
        PushStaticParamsToVFX();
    }

    private void InitBuffers()
    {
        // Release old
        positionsBuffer?.Dispose();
        colorsBuffer?.Dispose();
        depthBuffer?.Release();
        validCountBuffer?.Release();
        visibleCountBuffer?.Release();

        // Texture
        if (rgbTexture == null || rgbTexture.width != width || rgbTexture.height != height)
            rgbTexture = new Texture2D(Mathf.Max(1, width), Mathf.Max(1, height), TextureFormat.RGB24, false);

        // Capacity
        int capacity = Mathf.Max(1, width * height);

        // Depth
        depthBuffer = new ComputeBuffer(capacity, sizeof(uint));
        depthCPU = new uint[capacity];

        // Positions / Colors (Structured)
        positionsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(float) * 3);
        colorsBuffer    = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(float) * 4);
        bufferCapacity = capacity;

        // Counters
        validCountBuffer   = new ComputeBuffer(1, sizeof(uint));
        visibleCountBuffer = new ComputeBuffer(1, sizeof(uint));

        // Static compute bindings
        if (pointCloudCompute != null && csKernel >= 0)
        {
            pointCloudCompute.SetBuffer(csKernel, "depthBuffer",    depthBuffer);
            pointCloudCompute.SetBuffer(csKernel, "Positions",      positionsBuffer);
            pointCloudCompute.SetBuffer(csKernel, "Colors",         colorsBuffer);
            pointCloudCompute.SetBuffer(csKernel, "_ValidCount",    validCountBuffer);
            pointCloudCompute.SetBuffer(csKernel, "_VisibleCount",  visibleCountBuffer);
        }

        // Prime VFX with buffers
        if (vfx != null)
        {
            // These will warn if properties don't exist. Make sure your VFX graph has them.
            vfx.SetGraphicsBuffer(ID_Positions, positionsBuffer);
            vfx.SetGraphicsBuffer(ID_Colors,    colorsBuffer);
            vfx.Reinit(); // refresh after property changes / new buffers
        }

        Debug.Log($"[Renderer] Init buffers with size {width}x{height} (capacity {capacity})");
    }

    void Update()
    {
        HandleControllers();

        renderFrameCounter++;

        // Pull latest frame
        if (receiver != null && receiver.TryGetLatest(out var pkt) && pkt.IsValid)
        {
            bool sizeChanged = (pkt.width != width) || (pkt.height != height);
            width = pkt.width;
            height = pkt.height;

            fx = pkt.fx; fy = pkt.fy; cx = pkt.cx; cy = pkt.cy;
            cullMin = pkt.cullMin; cullMax = pkt.cullMax; xCull = pkt.xCull; yCull = pkt.yCull;

            if (sizeChanged || bufferCapacity < width * height)
                InitBuffers();

            // RGB (compressed → Texture2D)
            rgbTexture.LoadImage(pkt.rgbBytes);

            // Depth (ushort -> uint buffer)
            SetDepthBuffer(pkt.depthBytes);

            // Compute
            DispatchComputeShader();

            streamFrameCounter++;
        }

        // Feed VFX params (clamp to capacity)
        if (vfx != null)
        {
            int countForVFX = Mathf.Clamp(visiblePoints, 0, Mathf.Min(vfxCapacity, bufferCapacity));
            vfx.SetInt(ID_Count, countForVFX);
            vfx.SetFloat(ID_PointSizeWorld, pointSizeWorld);

            // Optional rebinds if you want to be extra safe each frame:
            // vfx.SetGraphicsBuffer(ID_Positions, positionsBuffer);
            // vfx.SetGraphicsBuffer(ID_Colors,    colorsBuffer);
        }

        if (logPerformance) LogFps();
    }

    private void SetDepthBuffer(byte[] latestDepthBytes)
    {
        int count = latestDepthBytes.Length / 2;
        if (depthCPU == null || depthCPU.Length < count) depthCPU = new uint[count];

        var depthUshorts = new ushort[count];
        Buffer.BlockCopy(latestDepthBytes, 0, depthUshorts, 0, latestDepthBytes.Length);
        for (int i = 0; i < count; i++) depthCPU[i] = depthUshorts[i];

        depthBuffer.SetData(depthCPU, 0, 0, count);
    }

    private void DispatchComputeShader()
    {
        if (pointCloudCompute == null || csKernel < 0) return;

        // Zero counters
        validCountCPU[0] = 0;
        visibleCountCPU[0] = 0;
        validCountBuffer.SetData(validCountCPU);
        visibleCountBuffer.SetData(visibleCountCPU);

        // Uniforms
        pointCloudCompute.SetInt("_Width", width);
        pointCloudCompute.SetInt("_Height", height);
        pointCloudCompute.SetFloat("_Fx", fx);
        pointCloudCompute.SetFloat("_Fy", fy);
        pointCloudCompute.SetFloat("_Cx", cx);
        pointCloudCompute.SetFloat("_Cy", cy);
        pointCloudCompute.SetFloat("_Scale", scale);
        pointCloudCompute.SetFloat("_CullMinZ", cullMin); // meters
        pointCloudCompute.SetFloat("_CullMaxZ", cullMax); // meters
        pointCloudCompute.SetFloat("_CullX", xCull);
        pointCloudCompute.SetFloat("_CullY", yCull);

        // Pose
        poseMatrix = Matrix4x4.TRS(renderingLocation, renderingRotation, Vector3.one);
        pointCloudCompute.SetMatrix("_PoseMatrix", poseMatrix);

        // Camera VP
        var cam = Camera.main;
        if (cam != null)
        {
            Matrix4x4 VP = cam.projectionMatrix * cam.worldToCameraMatrix;
            pointCloudCompute.SetMatrix("_VP", VP);
        }

        // Color
        if (rgbTexture != null)
        {
            pointCloudCompute.SetTexture(csKernel, "_ColorTex", rgbTexture);
            pointCloudCompute.SetInt("_UseColorTex", 1);
        }
        else
        {
            pointCloudCompute.SetInt("_UseColorTex", 0);
        }

        // Frustum toggle: start disabled while debugging visibility
        pointCloudCompute.SetInt("_DoFrustum", 0);

        int tgx = Mathf.Max(1, (width  + 7) / 8);
        int tgy = Mathf.Max(1, (height + 7) / 8);
        pointCloudCompute.Dispatch(csKernel, tgx, tgy, 1);

        // Read back ~2x/sec
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

    private void PushStaticParamsToVFX()
    {
        if (vfx == null) return;

        // Initial push (names must exist in the VFX Graph)
        vfx.SetFloat(ID_PointSizeWorld, pointSizeWorld);
        vfx.SetInt(ID_Count, 0);
        if (positionsBuffer != null) vfx.SetGraphicsBuffer(ID_Positions, positionsBuffer);
        if (colorsBuffer != null)    vfx.SetGraphicsBuffer(ID_Colors,    colorsBuffer);

        // Helpful when graph changed or properties were added
        vfx.Reinit();
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
        positionsBuffer?.Dispose();
        colorsBuffer?.Dispose();
        depthBuffer?.Release();
        validCountBuffer?.Release();
        visibleCountBuffer?.Release();
    }

    // Re-init on focus changes (common on Quest resume)
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus) InitBuffers();
    }

    void OnApplicationPause(bool pause)
    {
        if (!pause) InitBuffers();
    }
}