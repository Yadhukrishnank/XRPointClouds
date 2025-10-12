using UnityEngine;
using UnityEngine.VFX;
using System;
using System.Linq;
using System.Collections.Generic;

/// <summary>
/// Renders N cameras from a single multiplexed receiver by dispatching the same
/// compute once per camera into the SAME output buffers (Positions/Colors).
/// One VFX graph draws the merged cloud.
/// Includes rich diagnostics to trace "no points visible" issues.
/// </summary>
public class MultiCamPointCloudRenderer : MonoBehaviour
{
    [Header("References")]
    public ZmqFrameReceiver receiver;          // one-port, multiplexed
    public ComputeShader pointCloudCompute;    // CubeRendering.compute
    public VisualEffect vfx;                   // VFX Graph with exposed buffers & Count

    [Header("Capacity")]
    [Tooltip("Must be >= sum of all cameras' pixel counts.")]
    public int vfxCapacity = 750_000;

    [Header("Shader/VFX Property Names (must match VFX Graph)")]
    public string positionsName   = "Positions";
    public string colorsName      = "Colors";
    public string countName       = "Count";           // VFX exposed int
    public string depthBufName    = "depthBuffer";
    public string poseName        = "_PoseMatrix";
    public string colorTexName    = "_ColorTex";
    public string useColorName    = "_UseColorTex";

    [Header("Options")]
    public bool useColor = true;
    public bool doFrustumTest = false;        // disable while debugging poses
    public bool flipPosX = false, flipPosY = true;
    public bool flipRgbX = false, flipRgbY = true;

    [Header("Diagnostics")]
    public bool verboseFrameLogs = true;
    public bool logPerDispatch = true;
    public bool warnOnDepthMismatch = true;
    public bool enableF1TestSlab = true;

    // IDs / kernel
    private int csKernel = -1;
    private int ID_Positions, ID_Colors, ID_Count;

    // GPU resources
    private GraphicsBuffer positionsBuffer, colorsBuffer;
    private ComputeBuffer depthBuffer, validCountBuffer, visibleCountBuffer;

    // CPU scratch
    private readonly uint[] counter = new uint[1];

    // book-keeping
    private int bufferCapacity = 0;
    private int lastDepthElems = 0;
    private bool loggedFirstDraw = false;

    // Per-camera caches (textures & CPU depth buffers to avoid allocs)
    private readonly Dictionary<int, Texture2D> _rgbByCam = new();
    private readonly Dictionary<int, uint[]> _depthCpuByCam = new();

    // throttles
    private float _nextNoFrameLogAt = 0f;
    private float _nextCameraMainWarnAt = 0f;

    void Awake()
    {
        if (!pointCloudCompute || !vfx || !receiver)
        {
            Debug.LogError("[PCR] Assign Compute/VFX/Receiver in Inspector.");
            enabled = false; return;
        }

        csKernel = pointCloudCompute.FindKernel("CSMain");
        if (csKernel < 0) { Debug.LogError("[PCR] Kernel 'CSMain' not found in compute."); enabled = false; return; }

        ID_Positions = Shader.PropertyToID(positionsName);
        ID_Colors    = Shader.PropertyToID(colorsName);
        ID_Count     = Shader.PropertyToID(countName);

        Debug.Log($"[PCR] Init: VFX props: Count='{countName}', Pos='{positionsName}', Col='{colorsName}'");
    }

    void OnDestroy() => ReleaseAll();

    void ReleaseAll()
    {
        positionsBuffer?.Dispose(); positionsBuffer = null;
        colorsBuffer?.Dispose();    colorsBuffer = null;
        depthBuffer?.Dispose();     depthBuffer = null;
        validCountBuffer?.Dispose(); validCountBuffer = null;
        visibleCountBuffer?.Dispose(); visibleCountBuffer = null;

        foreach (var kv in _rgbByCam) if (kv.Value) Destroy(kv.Value);
        _rgbByCam.Clear();
        _depthCpuByCam.Clear();
    }

    // ---------- buffers ----------
    void EnsureBuffers(int pointsCapacity, int depthElems)
    {
        bool need = (pointsCapacity != bufferCapacity) || (depthElems != lastDepthElems) || positionsBuffer == null;

        if (!need) return;

        positionsBuffer?.Dispose();
        colorsBuffer?.Dispose();
        depthBuffer?.Dispose();
        validCountBuffer?.Dispose();
        visibleCountBuffer?.Dispose();

        bufferCapacity = Mathf.Max(pointsCapacity, 1);
        lastDepthElems = Mathf.Max(depthElems, 1);

        positionsBuffer    = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCapacity, sizeof(float) * 3);
        colorsBuffer       = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCapacity, sizeof(float) * 4);
        depthBuffer        = new ComputeBuffer(lastDepthElems, sizeof(uint));          // StructuredBuffer<uint>
        validCountBuffer   = new ComputeBuffer(1, sizeof(uint));
        visibleCountBuffer = new ComputeBuffer(1, sizeof(uint));

        // static binds
        pointCloudCompute.SetBuffer(csKernel, positionsName, positionsBuffer);
        pointCloudCompute.SetBuffer(csKernel, colorsName,    colorsBuffer);
        pointCloudCompute.SetBuffer(csKernel, depthBufName,  depthBuffer);
        pointCloudCompute.SetBuffer(csKernel, "_ValidCount",   validCountBuffer);
        pointCloudCompute.SetBuffer(csKernel, "_VisibleCount", visibleCountBuffer);

        // VFX buffers
        vfx.SetGraphicsBuffer(ID_Positions, positionsBuffer);
        vfx.SetGraphicsBuffer(ID_Colors,    colorsBuffer);
        vfx.Reinit(); // ensure VFX rebinds new buffers

        Debug.Log($"[PCR] EnsureBuffers: cap(points)={bufferCapacity}, cap(depthElems)={lastDepthElems}");
    }

    void ResetCounters()
    {
        counter[0] = 0u;
        validCountBuffer.SetData(counter);
        visibleCountBuffer.SetData(counter);
    }

    // ---------- utilities ----------
    private static string PktInfo(in FramePacket f)
        => $"cam={f.camId} {f.width}x{f.height} | rgb={f.rgbBytes?.Length ?? 0}B depth={f.depthBytes?.Length ?? 0}B | fx={f.fx:0.0} fy={f.fy:0.0} cx={f.cx:0.0} cy={f.cy:0.0} | Z[{f.cullMin:0.00},{f.cullMax:0.00}]";

    void SetDepthBuffer(int camId, byte[] depthBytes, int w, int h)
    {
        if (!_depthCpuByCam.TryGetValue(camId, out var depthCPU) || depthCPU == null || depthCPU.Length < w * h)
        {
            depthCPU = new uint[w * h];
            _depthCpuByCam[camId] = depthCPU;
        }

        int expected = w * h;
        int count = depthBytes.Length / 2;
        if (count != expected && warnOnDepthMismatch)
            Debug.LogWarning($"[PCR] Depth size mismatch (cam {camId}). got={count}, expected={expected}. Clamping & zero-padding.");

        count = Math.Min(count, expected);

        var tmpU16 = new ushort[count];
        Buffer.BlockCopy(depthBytes, 0, tmpU16, 0, count * 2);
        for (int i = 0; i < count; i++) depthCPU[i] = tmpU16[i];
        for (int i = count; i < expected; i++) depthCPU[i] = 0;

        if (depthBuffer == null || depthBuffer.count < expected)
        {
            depthBuffer?.Dispose();
            depthBuffer = new ComputeBuffer(expected, sizeof(uint));
            pointCloudCompute.SetBuffer(csKernel, depthBufName, depthBuffer);
            lastDepthElems = expected;
            Debug.Log($"[PCR] Depth buffer resized → {expected} elems.");
        }

        depthBuffer.SetData(depthCPU, 0, 0, expected);

        // tiny peek at first few depths to detect all-zero streams
        if (verboseFrameLogs && expected >= 4)
        {
            int nz = 0; for (int i = 0; i < Math.Min(64, expected); i++) if (depthCPU[i] != 0) { nz++; if (nz > 2) break; }
            Debug.Log($"[PCR] cam={camId} depth peek: first64 nonzero={nz}");
        }
    }

    void BindCommonUniforms()
    {
        pointCloudCompute.SetInt("_DoFrustum", doFrustumTest ? 1 : 0);

        var cam = Camera.main;
        if (cam == null)
        {
            if (Time.unscaledTime >= _nextCameraMainWarnAt)
            {
                Debug.LogWarning("[PCR] No Camera.main found. Frustum test OFF is recommended while debugging.");
                _nextCameraMainWarnAt = Time.unscaledTime + 2f;
            }
        }
        else
        {
            Matrix4x4 VP = cam.projectionMatrix * cam.worldToCameraMatrix;
            pointCloudCompute.SetMatrix("_VP", VP);
        }

        pointCloudCompute.SetInt("_FlipPosX", flipPosX ? 1 : 0);
        pointCloudCompute.SetInt("_FlipPosY", flipPosY ? 1 : 0);
        pointCloudCompute.SetInt("_FlipRgbX", flipRgbX ? 1 : 0);
        pointCloudCompute.SetInt("_FlipRgbY", flipRgbY ? 1 : 0);
    }

    void DispatchForPacket(in FramePacket pkt, ref int visibleBefore, int passIndex)
    {
        // RGB texture per camera (persist by size)
        if (!_rgbByCam.TryGetValue(pkt.camId, out var tex) || tex == null || tex.width != pkt.width || tex.height != pkt.height)
        {
            if (tex) Destroy(tex);
            tex = new Texture2D(Mathf.Max(1, pkt.width), Mathf.Max(1, pkt.height), TextureFormat.RGB24, false);
            _rgbByCam[pkt.camId] = tex;
            if (verboseFrameLogs) Debug.Log($"[PCR] cam={pkt.camId} RGB texture (re)created: {pkt.width}x{pkt.height}");
        }
        if (useColor && pkt.rgbBytes != null && pkt.rgbBytes.Length > 0)
        {
            bool ok = tex.LoadImage(pkt.rgbBytes);
            pointCloudCompute.SetTexture(csKernel, colorTexName, tex);
            pointCloudCompute.SetInt(useColorName, ok ? 1 : 0);
            if (verboseFrameLogs && !ok) Debug.LogWarning($"[PCR] cam={pkt.camId} LoadImage failed; disabling color this dispatch.");
        }
        else
        {
            pointCloudCompute.SetInt(useColorName, 0);
        }

        // Depth
        SetDepthBuffer(pkt.camId, pkt.depthBytes, pkt.width, pkt.height);

        // Per-dispatch uniforms
        pointCloudCompute.SetInt("_Width",  pkt.width);
        pointCloudCompute.SetInt("_Height", pkt.height);
        pointCloudCompute.SetFloat("_Fx", pkt.fx);
        pointCloudCompute.SetFloat("_Fy", pkt.fy);
        pointCloudCompute.SetFloat("_Cx", pkt.cx);
        pointCloudCompute.SetFloat("_Cy", pkt.cy);

        // Z & XY ROI
        pointCloudCompute.SetFloat("_CullMinZ", pkt.cullMin);
        pointCloudCompute.SetFloat("_CullMaxZ", pkt.cullMax);
        pointCloudCompute.SetFloat("_CullX", pkt.xCull);
        pointCloudCompute.SetFloat("_CullY", pkt.yCull);

        pointCloudCompute.SetMatrix(poseName, pkt.pose);
        pointCloudCompute.SetFloat("_Scale", 1.0f); // keep meter units

        // Dispatch
        int tgx = Mathf.Max(1, (pkt.width  + 7) / 8);
        int tgy = Mathf.Max(1, (pkt.height + 7) / 8);
        if (verboseFrameLogs) Debug.Log($"[PCR] DISPATCH cam={pkt.camId} groups=({tgx},{tgy},1) → {pkt.width}x{pkt.height}");

        pointCloudCompute.Dispatch(csKernel, tgx, tgy, 1);

        if (logPerDispatch)
        {
            visibleCountBuffer.GetData(counter);
            int now = (int)counter[0];
            Debug.Log($"[PCR] cam={pkt.camId} pass#{passIndex} appended={now - visibleBefore} totalVisible={now}");
            visibleBefore = now;
        }
    }

    // ---------- synthetic test slab (F1) ----------
    void DrawTestSlabIfRequested()
    {
        if (!enableF1TestSlab) return;
        if (!Input.GetKeyDown(KeyCode.F1)) return;

        int W = 64, H = 64; // 4096 pts
        EnsureBuffers(W * H, W * H);

        // fake depth ramp (in mm)
        uint[] z = new uint[W * H];
        for (int v = 0; v < H; v++)
            for (int u = 0; u < W; u++)
                z[v * W + u] = (uint)(1000 + 5 * v);  // 1.0m.. ~1.3m

        depthBuffer.SetData(z);
        ResetCounters();
        BindCommonUniforms();

        // identity-ish intrinsics
        pointCloudCompute.SetInt("_Width", W);
        pointCloudCompute.SetInt("_Height", H);
        pointCloudCompute.SetFloat("_Fx", 0.9f * W);
        pointCloudCompute.SetFloat("_Fy", 0.9f * H);
        pointCloudCompute.SetFloat("_Cx", 0.5f * W);
        pointCloudCompute.SetFloat("_Cy", 0.5f * H);
        pointCloudCompute.SetFloat("_CullMinZ", 0.1f);
        pointCloudCompute.SetFloat("_CullMaxZ", 5.0f);
        pointCloudCompute.SetFloat("_CullX", 0); pointCloudCompute.SetFloat("_CullY", 0);
        pointCloudCompute.SetFloat("_Scale", 1.0f);
        pointCloudCompute.SetInt(useColorName, 0);
        pointCloudCompute.SetMatrix(poseName, Matrix4x4.TRS(new Vector3(0, 0, 0.5f), Quaternion.identity, Vector3.one));

        int tgx = (W + 7) / 8, tgy = (H + 7) / 8;
        pointCloudCompute.Dispatch(csKernel, tgx, tgy, 1);

        visibleCountBuffer.GetData(counter);
        int visible = Mathf.Clamp((int)counter[0], 0, Mathf.Min(W * H, vfxCapacity));
        vfx.SetInt(ID_Count, visible);

        Debug.Log($"[PCR] TEST SLAB: dispatched {W}x{H}, visible={visible}. If you still see nothing, your VFX binding/nodes need checking.");
    }

    // ---------- main update ----------
    void Update()
    {
        DrawTestSlabIfRequested();

        if (pointCloudCompute == null || vfx == null || receiver == null)
        {
            Debug.LogError("[PCR] Missing references.");
            return;
        }

        var frames = receiver.GetAllLatest();
        if (frames == null || frames.Length == 0)
        {
            vfx.SetInt(ID_Count, 0);
            if (Time.unscaledTime >= _nextNoFrameLogAt)
            {
                Debug.Log("[PCR] No frames yet… (press F1 to draw a test slab).");
                _nextNoFrameLogAt = Time.unscaledTime + 1.0f;
            }
            return;
        }

        // total capacity
        int totalPts = 0;
        int maxDepthElems = 0;
        foreach (var f in frames) { totalPts += Mathf.Max(0, f.width * f.height); maxDepthElems = Math.Max(maxDepthElems, f.width * f.height); }
        EnsureBuffers(totalPts, maxDepthElems);

        // zero counters & common uniforms
        ResetCounters();
        BindCommonUniforms();

        // set VP (again, in case camera changed this frame)
        var cam = Camera.main;
        if (cam)
        {
            var VP = cam.projectionMatrix * cam.worldToCameraMatrix;
            pointCloudCompute.SetMatrix("_VP", VP);
        }
        else if (doFrustumTest)
        {
            Debug.LogWarning("[PCR] doFrustumTest is ON but no Camera.main — points will likely be culled.");
        }

        // sort by cam id for a stable append order
        Array.Sort(frames, (a, b) => a.camId.CompareTo(b.camId));

        if (verboseFrameLogs) Debug.Log($"[PCR] ----- frame start | cams={frames.Length} totalCap={totalPts} -----");
        int visibleBefore = 0;
        int pass = 0;
        foreach (var pkt in frames)
        {
            if (verboseFrameLogs) Debug.Log("[PCR] " + PktInfo(pkt));
            if (!pkt.IsValid)
            {
                Debug.LogWarning($"[PCR] Skipping invalid packet cam={pkt.camId}");
                continue;
            }
            DispatchForPacket(pkt, ref visibleBefore, ++pass);
        }

        // final counts → VFX
        validCountBuffer.GetData(counter);   int validFinal   = (int)counter[0];
        visibleCountBuffer.GetData(counter); int visibleFinal = (int)counter[0];

        int vfxCount = Mathf.Clamp(visibleFinal, 0, Mathf.Min(totalPts, vfxCapacity));
        vfx.SetInt(ID_Count, vfxCount);

        if (verboseFrameLogs)
        {
            Debug.Log($"[PCR] frame end | Valid={validFinal} Visible={visibleFinal} → VFX.Count={vfxCount} (cap={vfxCapacity})");
            if (visibleFinal == 0)
            {
                string hints =
                    "- Are depth values all zero (check 'depth peek' logs)?\n" +
                    "- Is Z cull too strict? (_CullMinZ/_CullMaxZ from sender)\n" +
                    "- Is frustum test ON with a wrong pose? Disable 'doFrustumTest'.\n" +
                    "- Do VFX property names match? (Count/Positions/Colors)\n" +
                    "- Does your VFX graph spawn/output when Count>0?";
                Debug.LogWarning("[PCR] Visible==0. Hints:\n" + hints);
            }
        }

        if (!loggedFirstDraw && visibleFinal > 0)
        {
            loggedFirstDraw = true;
            Debug.Log($"[PCR] First nonzero draw ✓ Visible={visibleFinal}");
        }
    }
}
