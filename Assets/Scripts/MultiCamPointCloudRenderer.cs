// Merges N RGB-D streams into one point cloud via compute shader + VFX Graph.
// Flips are EXACTLY the same as the old single-camera pipeline:
//   - FlipPosX / FlipPosY applied in camera space (before cull/pose)
//   - FlipRgbX / FlipRgbY applied on texture sampling (su/sv)
//   - No Z flip.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

public class MultiCamPointCloudRenderer : MonoBehaviour
{
    [Header("Data Source")]
    public MultiZmqFrameReceiver receiver;   // latest frame per cameraId

    [Header("Compute / Visuals")]
    public ComputeShader pointCloudCompute;  // must have: #pragma kernel CSMain
    [SerializeField] string kernelName = "CSMain";
    public VisualEffect vfx;                 // VFX reads Positions/Colors buffers + Count
    [Range(0.001f, 0.2f)] public float pointSizeWorld = 0.01f;
    public int vfxCapacity = 2_000_000;

    [Header("Culling (meters)")]
    public float scale = 1.0f;
    public float cullMin = 0.10f;
    public float cullMax = 8.0f;
    public float xCull = 0.0f;   // 0 disables
    public float yCull = 0.0f;   // 0 disables
    public bool doFrustumTest = true;

    [Header("Image ↔ Unity flips (same as previous pipeline)")]
    public bool flipPosX = false;
    public bool flipPosY = true;     // default ON (matches your old setup)
    public bool flipRgbX = false;
    public bool flipRgbY = true;     // default ON (matches your old setup)

    [Header("Poses")]
    [Tooltip("If true, use per-packet Twc pose; else use this GameObject's transform.")]
    public bool usePacketPosesIfPresent = true;

    [Header("Debug")]
    public bool logKernelInfo = true;
    public bool logIntrinsics = true;
    public bool logDepthStats = false;
    public float statsLogEverySeconds = 1.0f;

    // ----- internals -----
    int csKernel = -1;
    uint kGroupX, kGroupY, kGroupZ;
    float lastLogTime = -999f;
    float lastCounterSample = 0f;
    int visiblePoints = 0;

    class CamRes
    {
        public int camId, width, height, capacity;
        public Texture2D rgbTexture;     // per-cam color
        public ComputeBuffer depthU32;   // per-cam depth (uint[]) converted from ushort
    }
    readonly Dictionary<int, CamRes> cams = new();

    // Merged outputs
    GraphicsBuffer positions;            // float3 (12B stride)
    GraphicsBuffer colors;               // float4 (16B stride)
    ComputeBuffer validCount;            // uint[1]
    ComputeBuffer visibleCount;          // uint[1]
    uint[] validCountCPU = new uint[1];
    uint[] visibleCountCPU = new uint[1];
    int totalCapacity = 0;

    // Temp CPU buffers
    uint[] tmpDepthU32;
    ushort[] tmpDepthU16;

    // VFX IDs
    static readonly int ID_PointSizeWorld = Shader.PropertyToID("PointSizeWorld");
    static readonly int ID_Count = Shader.PropertyToID("Count");
    static readonly int ID_Positions = Shader.PropertyToID("Positions");
    static readonly int ID_Colors = Shader.PropertyToID("Colors");

    void Awake()
    {
        if (!SystemInfo.supportsComputeShaders)
            Debug.LogError("[MultiCamPCR] This GPU/Graphics API does not support compute shaders.");
    }

    void OnEnable() => ResolveKernel(true);
    void OnValidate() => ResolveKernel(false);
    void Start() => ResolveKernel(false);

    void OnDestroy()
    {
        foreach (var c in cams.Values)
        {
            if (c.rgbTexture != null) Destroy(c.rgbTexture); // Texture2D => Destroy
            c.depthU32?.Release();
        }
        positions?.Dispose();
        colors?.Dispose();
        validCount?.Release();
        visibleCount?.Release();
    }

    void ResolveKernel(bool verbose)
    {
        csKernel = -1; kGroupX = kGroupY = kGroupZ = 0;
        if (pointCloudCompute == null)
        {
            if (verbose) Debug.LogError("[MultiCamPCR] ComputeShader not assigned.");
            return;
        }
        try
        {
            csKernel = pointCloudCompute.FindKernel(kernelName);
            pointCloudCompute.GetKernelThreadGroupSizes(csKernel, out kGroupX, out kGroupY, out kGroupZ);
            if (verbose && logKernelInfo)
                Debug.Log($"[MultiCamPCR] Using compute '{pointCloudCompute.name}' kernel '{kernelName}' index={csKernel} groups=({kGroupX},{kGroupY},{kGroupZ})");
        }
        catch (Exception ex)
        {
            csKernel = -1;
            if (verbose) Debug.LogError($"[MultiCamPCR] Kernel '{kernelName}' invalid in '{pointCloudCompute?.name}'. {ex.Message}");
        }
    }

    bool TryDispatch(int tgx, int tgy, int tgz = 1)
    {
        if (pointCloudCompute == null) return false;
        if (csKernel < 0) { ResolveKernel(true); if (csKernel < 0) return false; }
        if (tgx < 1 || tgy < 1 || tgz < 1) { Debug.LogWarning($"[MultiCamPCR] Skip dispatch: ({tgx},{tgy},{tgz})"); return false; }
        pointCloudCompute.Dispatch(csKernel, tgx, tgy, tgz);
        return true;
    }

    void Update()
    {
        if (receiver == null || pointCloudCompute == null || csKernel < 0) return;

        var frames = receiver.SnapshotAll(); // latest per cam
        if (frames == null || frames.Count == 0) return;

        // 1) Ensure per-cam resources & big-buffer capacity
        int requiredTotal = 0;
        foreach (var pkt in frames)
        {
            var cr = GetOrCreateCam(pkt.camId, pkt.width, pkt.height);
            requiredTotal += cr.capacity;
        }
        if (requiredTotal > totalCapacity)
        {
            AllocateOutputs(requiredTotal);
            vfx?.Reinit();
        }

        // 2) Zero counters ONCE per frame
        validCountCPU[0] = 0; visibleCountCPU[0] = 0;
        validCount.SetData(validCountCPU);
        visibleCount.SetData(visibleCountCPU);

        // 3) For each camera: upload depth/color, set uniforms, dispatch
        foreach (var pkt in frames)
        {
            var cr = cams[pkt.camId];

            // Color (JPEG -> Texture2D on main thread)
            cr.rgbTexture.LoadImage(pkt.rgbBytes);

            // Depth: bytes -> ushort[] -> uint[] -> GPU
            int count = cr.capacity;
            EnsureTempDepth(count);
            Buffer.BlockCopy(pkt.depthBytes, 0, tmpDepthU16, 0, count * 2);
            for (int i = 0; i < count; i++) tmpDepthU32[i] = tmpDepthU16[i];
            cr.depthU32.SetData(tmpDepthU32, 0, 0, count);

            // Occasional logs
            if (statsLogEverySeconds > 0f && Time.unscaledTime - lastLogTime > statsLogEverySeconds)
            {
                if (logIntrinsics)
                    Debug.Log($"[cam {pkt.camId}] {cr.width}x{cr.height} fx={pkt.fx:F1} fy={pkt.fy:F1} cx={pkt.cx:F1} cy={pkt.cy:F1}");
                if (logDepthStats)
                {
                    ushort dmin = ushort.MaxValue, dmax = 0;
                    for (int i = 0; i < count; i++) { var d = tmpDepthU16[i]; if (d != 0) { if (d < dmin) dmin = d; if (d > dmax) dmax = d; } }
                    Debug.Log($"[cam {pkt.camId}] depth mm ~ min={dmin} max={dmax}");
                }
                lastLogTime = Time.unscaledTime;
            }

            // Bind per-cam inputs
            pointCloudCompute.SetBuffer(csKernel, "depthBuffer", cr.depthU32);
            pointCloudCompute.SetInt("_Width", cr.width);
            pointCloudCompute.SetInt("_Height", cr.height);

            pointCloudCompute.SetFloat("_Fx", pkt.hasIntr ? pkt.fx : 0f);
            pointCloudCompute.SetFloat("_Fy", pkt.hasIntr ? pkt.fy : 0f);
            pointCloudCompute.SetFloat("_Cx", pkt.hasIntr ? pkt.cx : 0f);
            pointCloudCompute.SetFloat("_Cy", pkt.hasIntr ? pkt.cy : 0f);

            pointCloudCompute.SetFloat("_Scale", scale);
            pointCloudCompute.SetFloat("_CullMinZ", cullMin);
            pointCloudCompute.SetFloat("_CullMaxZ", cullMax);
            pointCloudCompute.SetFloat("_CullX", xCull);
            pointCloudCompute.SetFloat("_CullY", yCull);

            // Pose
            Matrix4x4 pose = (usePacketPosesIfPresent && pkt.hasPose)
                             ? pkt.pose
                             : Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            pointCloudCompute.SetMatrix("_PoseMatrix", pose);

            // VP
            var cam = Camera.main;
            if (cam != null)
            {
                Matrix4x4 VP = cam.projectionMatrix * cam.worldToCameraMatrix;
                pointCloudCompute.SetMatrix("_VP", VP);
            }

            // EXACT flip bindings (same names as old pipeline)
            pointCloudCompute.SetInt("_FlipPosX", flipPosX ? 1 : 0);
            pointCloudCompute.SetInt("_FlipPosY", flipPosY ? 1 : 0);
            pointCloudCompute.SetInt("_FlipRgbX", flipRgbX ? 1 : 0);
            pointCloudCompute.SetInt("_FlipRgbY", flipRgbY ? 1 : 0);

            // Color usage & frustum toggle
            pointCloudCompute.SetTexture(csKernel, "_ColorTex", cr.rgbTexture);
            pointCloudCompute.SetInt("_UseColorTex", 1);
            pointCloudCompute.SetInt("_DoFrustum", doFrustumTest ? 1 : 0);

            // Dispatch
            int tgx = Mathf.Max(1, (cr.width + 7) / 8);
            int tgy = Mathf.Max(1, (cr.height + 7) / 8);
            if (!TryDispatch(tgx, tgy)) break;
        }

        // 4) Read back counters ~2x/sec
        if (Time.unscaledTime - lastCounterSample > 0.5f)
        {
            validCount.GetData(validCountCPU);
            visibleCount.GetData(visibleCountCPU);
            visiblePoints = (int)visibleCountCPU[0];
            lastCounterSample = Time.unscaledTime;
        }

        // 5) VFX feed
        if (vfx != null)
        {
            int countForVFX = Mathf.Clamp(visiblePoints, 0, Mathf.Min(vfxCapacity, totalCapacity));
            vfx.SetInt(ID_Count, countForVFX);
            vfx.SetFloat(ID_PointSizeWorld, pointSizeWorld);
        }
    }

    CamRes GetOrCreateCam(int camId, int w, int h)
    {
        if (!cams.TryGetValue(camId, out var cr))
        {
            cr = new CamRes { camId = camId };
            cams[camId] = cr;
        }
        cr.width = Mathf.Max(1, w);
        cr.height = Mathf.Max(1, h);
        int cap = cr.width * cr.height;

        if (cr.rgbTexture == null || cr.rgbTexture.width != cr.width || cr.rgbTexture.height != cr.height)
        {
            if (cr.rgbTexture != null) Destroy(cr.rgbTexture);
            cr.rgbTexture = new Texture2D(cr.width, cr.height, TextureFormat.RGBA32, false, false);
        }
        if (cr.depthU32 == null || cr.depthU32.count < cap)
        {
            cr.depthU32?.Release();
            cr.depthU32 = new ComputeBuffer(cap, sizeof(uint), ComputeBufferType.Structured);
        }

        cr.capacity = cap;
        return cr;
    }

    void AllocateOutputs(int requiredTotalCapacity)
    {
        totalCapacity = Mathf.Max(1, requiredTotalCapacity);

        positions?.Dispose();
        colors?.Dispose();
        validCount?.Release();
        visibleCount?.Release();

        // IMPORTANT: float3 (12B) for Positions, float4 (16B) for Colors
        positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalCapacity, sizeof(float) * 3);
        colors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalCapacity, sizeof(float) * 4);
        validCount = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
        visibleCount = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);

        // Bind once (static across dispatches)
        pointCloudCompute.SetBuffer(csKernel, "Positions", positions);
        pointCloudCompute.SetBuffer(csKernel, "Colors", colors);
        pointCloudCompute.SetBuffer(csKernel, "_ValidCount", validCount);
        pointCloudCompute.SetBuffer(csKernel, "_VisibleCount", visibleCount);

        // VFX hookup
        if (vfx != null)
        {
            vfx.SetGraphicsBuffer(ID_Positions, positions);
            vfx.SetGraphicsBuffer(ID_Colors, colors);
            vfx.SetFloat(ID_PointSizeWorld, pointSizeWorld);
            vfx.SetInt(ID_Count, 0);
        }
    }

    void EnsureTempDepth(int count)
    {
        if (tmpDepthU16 == null || tmpDepthU16.Length < count) tmpDepthU16 = new ushort[count];
        if (tmpDepthU32 == null || tmpDepthU32.Length < count) tmpDepthU32 = new uint[count];
    }
}
