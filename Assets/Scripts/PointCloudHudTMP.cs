// PointCloudHudTMP.cs
// Unity 6000+ / Quest-friendly HUD for PointCloudRenderer
// - Works with TMP_Text or legacy UnityEngine.UI.Text
// - Auto-discovers PointCloudRenderer (even when spawned later by anchors)
// - No deprecated API usage

using System.Text;
using UnityEngine;

#if TMP_PRESENT || UNITY_2018_4_OR_NEWER
using TMPro;
#endif

#if UNITY_2019_1_OR_NEWER
using UnityEngine.UI;
#endif

[DisallowMultipleComponent]
public class PointCloudHudTMP : MonoBehaviour
{
    [Header("References (optional)")]
#if TMP_PRESENT || UNITY_2018_4_OR_NEWER
    [SerializeField] private TMP_Text tmpText;       // Assign if using TextMeshPro (recommended)
#endif
#if UNITY_2019_1_OR_NEWER
    [SerializeField] private Text uiText;            // Assign if using legacy UI.Text
#endif
    [SerializeField] private PointCloudRenderer rendererRef; // Can be left empty (auto-find)

    [Header("Behaviour")]
    [Tooltip("How often we re-try discovery while the prefab may not be spawned yet.")]
    [SerializeField, Min(0.05f)] private float findInterval = 0.5f;

    [Tooltip("Show extra detail like densities and point size.")]
    [SerializeField] private bool verbose = true;

    private float _nextFindAt = 0f;
    private readonly StringBuilder _sb = new StringBuilder(256);

    void Awake()
    {
        // Auto-pick a text component on this GameObject if none assigned.
#if TMP_PRESENT || UNITY_2018_4_OR_NEWER
        if (!tmpText) tmpText = GetComponent<TMP_Text>();
#endif
#if UNITY_2019_1_OR_NEWER
        if (!uiText) uiText = GetComponent<Text>();
#endif
    }

    void OnEnable()
    {
        ClearText();
        // Try an immediate resolve
        TryResolveRenderer();
    }

    void Update()
    {
        // Keep trying to resolve until we have a renderer (anchor prefab may appear later)
        if (!rendererRef && Time.unscaledTime >= _nextFindAt)
        {
            TryResolveRenderer();
            _nextFindAt = Time.unscaledTime + findInterval;
        }

        // Update HUD
        if (rendererRef)
        {
            var r = rendererRef;

            _sb.Length = 0;
            // Line 1: core counts & fps
            _sb.Append("Pts ");
            _sb.Append(r.VisiblePoints.ToString("n0"));
            _sb.Append(" / ");
            _sb.Append(r.ValidPoints.ToString("n0"));
            _sb.Append("   |   Fps R:");
            _sb.Append(r.LastRenderFps.ToString("0"));
            _sb.Append(" S:");
            _sb.Append(r.LastStreamFps.ToString("0"));

            // Line 2: frame + extras
            _sb.Append("\nFrame ");
            _sb.Append(r.FrameWidth);
            _sb.Append("x");
            _sb.Append(r.FrameHeight);

            if (verbose)
            {
                _sb.Append("   |   Vis ");
                _sb.Append((r.VisibleDensity01 * 100f).ToString("0.0"));
                _sb.Append("%, Val ");
                _sb.Append((r.ValidDensity01 * 100f).ToString("0.0"));
                _sb.Append("%");

                _sb.Append("   |   PtSize ");
                _sb.Append(r.pointSizeWorld.ToString("0.000"));
            }

            SetText(_sb.ToString());
        }
        else
        {
            SetText("Waiting for point cloud…");
        }
    }

    // ---------- Helpers ----------

    void TryResolveRenderer()
    {
        // First: if one already assigned, keep it.
        if (rendererRef) return;

        // Try modern, fast API (Unity 2023+/6000)
#if UNITY_2023_1_OR_NEWER
        rendererRef = Object.FindFirstObjectByType<PointCloudRenderer>(FindObjectsInactive.Exclude);
        if (!rendererRef)
            rendererRef = Object.FindAnyObjectByType<PointCloudRenderer>(FindObjectsInactive.Exclude);

        // As a last resort (if your anchor spawns inactive first), include inactive:
        if (!rendererRef)
            rendererRef = Object.FindFirstObjectByType<PointCloudRenderer>(FindObjectsInactive.Include);
#else
        // Older editors fallback
        rendererRef = Object.FindObjectOfType<PointCloudRenderer>();
#endif
    }

    void SetText(string s)
    {
#if TMP_PRESENT || UNITY_2018_4_OR_NEWER
        if (tmpText) { tmpText.text = s; return; }
#endif
#if UNITY_2019_1_OR_NEWER
        if (uiText) { uiText.text = s; return; }
#endif
        // No text component assigned or found; nothing to display.
    }

    void ClearText()
    {
        SetText(string.Empty);
    }

    // Optional: let other code assign the renderer when it spawns
    public void SetRenderer(PointCloudRenderer r)
    {
        rendererRef = r;
    }
}
