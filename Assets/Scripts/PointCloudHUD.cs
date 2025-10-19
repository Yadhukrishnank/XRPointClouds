using TMPro;
using UnityEngine;

[AddComponentMenu("HUD/Point Cloud HUD")]
[RequireComponent(typeof(TMP_Text))]
public class PointCloudHUD : MonoBehaviour
{
    [Header("Refs")]
    public MultiCamPointCloudRenderer rendererRef;  // drag in Inspector if you like
    public TMP_Text label;                          // auto-fills from this object

    [Header("Update Settings")]
    public float updateInterval = 0.25f;
    [Range(0.01f, 1f)] public float fpsSmoothing = 0.15f;

    float _timer;
    float _fpsEMA;

    void Reset()
    {
        if (!label) label = GetComponent<TMP_Text>();
        if (!rendererRef) rendererRef = FindRendererRuntimeSafe();
    }

    void Awake()
    {
        if (!label) label = GetComponent<TMP_Text>();
        if (!rendererRef) rendererRef = FindRendererRuntimeSafe();
    }

    // Use new API on newer Unity; fall back on older versions.
    static MultiCamPointCloudRenderer FindRendererRuntimeSafe()
    {
        #if UNITY_2023_1_OR_NEWER
            // Use Any for speed; First is fine too.
            return Object.FindAnyObjectByType<MultiCamPointCloudRenderer>();
        #else
            return Object.FindObjectOfType<MultiCamPointCloudRenderer>();
        #endif
    }

    void Update()
    {
        // FPS (EMA)
        float dt = Time.unscaledDeltaTime;
        float inst = dt > 0f ? 1f / dt : 0f;
        _fpsEMA = Mathf.Lerp(_fpsEMA, inst, fpsSmoothing);

        _timer += Time.unscaledDeltaTime;
        if (_timer < updateInterval || label == null || rendererRef == null) return;
        _timer = 0f;

        label.text =
            $"Visible: {rendererRef.CurrentVisibleCount}\n" +
            $"Valid:   {rendererRef.CurrentValidCount}\n" +
            $"FPS:     {_fpsEMA:0.0}";
    }
}
