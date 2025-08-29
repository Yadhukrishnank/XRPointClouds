using System;
using UnityEngine;
using UnityEngine.VFX;

[RequireComponent(typeof(VisualEffect))]
public class VfxPointCloudBridge : MonoBehaviour
{
    public ZmqFrameReceiver source;

    [Header("Format")]
    public bool depthIsUInt16MM = true;   // true = depth bytes are ushort millimeters
    public bool flipY = true;
    public uint stride = 1;

    [Header("Look")]
    public float particleSize = 0.004f;   // meters (Output=Quad)

    VisualEffect vfx;
    Texture2D colorTex, depthTex;
    int w = -1, h = -1;
    uint lastStride;

    // VFX property names
    const string P_ColorTex = "ColorTex";
    const string P_DepthTex = "DepthTex";
    const string P_W = "ImageWidth";
    const string P_H = "ImageHeight";
    const string P_Stride = "Stride";
    const string P_Size = "ParticleSize";
    const string P_PCount = "ParticleCount";
    const string P_Fx = "Fx";
    const string P_Fy = "Fy";
    const string P_Cx = "Cx";
    const string P_Cy = "Cy";
    const string P_DepthScale = "DepthScale";
    const string P_FlipY = "FlipY";

    void Awake() => vfx = GetComponent<VisualEffect>();

    void Update()
    {
        if (source == null || !source.TryGetLatest(out var pk) || !pk.IsValid) return;

        bool sizeChanged = (pk.width != w) || (pk.height != h);

        // (Re)allocate textures on size change
        if (sizeChanged || colorTex == null || depthTex == null)
        {
            w = pk.width; h = pk.height;

            colorTex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };

#if UNITY_ANDROID
            var depthFmt = (depthIsUInt16MM && SystemInfo.SupportsTextureFormat(TextureFormat.R16))
                           ? TextureFormat.R16 : TextureFormat.RFloat;
#else
            var depthFmt = depthIsUInt16MM ? TextureFormat.R16 : TextureFormat.RFloat;
#endif
            depthTex = new Texture2D(w, h, depthFmt, false, true)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        }

        // Color (JPEG)
        colorTex.LoadImage(pk.rgbBytes, false);

        // Depth
        int N = w * h;
        if (depthTex.format == TextureFormat.R16)
        {
            // raw ushort mm
            depthTex.LoadRawTextureData(pk.depthBytes); // length must be N*2
            depthTex.Apply(false, false);
        }
        else
        {
            // float meters (convert if source is mm)
            if (depthIsUInt16MM)
            {
                var f = new float[N];
                for (int i = 0, bi = 0; i < N; i++, bi += 2)
                    f[i] = 0.001f * BitConverter.ToUInt16(pk.depthBytes, bi);
                var bytes = new byte[N * 4];
                Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
                depthTex.LoadRawTextureData(bytes);
            }
            else depthTex.LoadRawTextureData(pk.depthBytes);
            depthTex.Apply(false, false);
        }

        // Set VFX properties
        vfx.SetTexture(P_ColorTex, colorTex);
        vfx.SetTexture(P_DepthTex, depthTex);

        vfx.SetUInt(P_W, (uint)w);
        vfx.SetUInt(P_H, (uint)h);
        vfx.SetUInt(P_Stride, stride);
        vfx.SetFloat(P_Size, particleSize);

        vfx.SetFloat(P_Fx, pk.fx); vfx.SetFloat(P_Fy, pk.fy);
        vfx.SetFloat(P_Cx, pk.cx); vfx.SetFloat(P_Cy, pk.cy);

        // Depth scale from texture format
        vfx.SetFloat(P_DepthScale, depthTex.format == TextureFormat.R16 ? 0.001f : 1.0f);

        // Flip toggle
        if (vfx.HasBool(P_FlipY)) vfx.SetBool(P_FlipY, flipY);
        else vfx.SetInt(P_FlipY, flipY ? 1 : 0);

        // Count and (re)spawn only when needed
        uint effW = (uint)(w / Mathf.Max(1, (int)stride));
        uint effH = (uint)(h / Mathf.Max(1, (int)stride));
        vfx.SetUInt(P_PCount, effW * effH);

        if (sizeChanged || lastStride != stride)
            vfx.Reinit();
        lastStride = stride;
    }
}
