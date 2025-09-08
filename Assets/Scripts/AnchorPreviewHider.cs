using System.Collections.Generic;
using UnityEngine;

public class AnchorPreviewHider : MonoBehaviour
{
    [Header("Behavior")]
    [SerializeField] private bool destroyOnSuccess = true; // destroy vs deactivate
    [SerializeField] private bool oneShot = true;          // hide after first placement only

    [Header("Fallback search (optional)")]
    [Tooltip("If you also keep a global preview under the controller, set its tag here.")]
    [SerializeField] private string fallbackPreviewTag = "AnchorPreview";

    private bool _placedOnce;

    // Called by Spatial Anchor Core event:
    // On Anchor Create Completed (OVRSpatialAnchor, OperationResult)
    public void OnAnchorCreateCompleted(OVRSpatialAnchor anchor, OVRSpatialAnchor.OperationResult result)
    {
        if (result != OVRSpatialAnchor.OperationResult.Success) return;
        if (_placedOnce && oneShot) return;

        _placedOnce = true;

        // 1) Try to find a PreviewMarker INSIDE the created anchor prefab
        GameObject target = null;
        if (anchor != null)
        {
            var marker = anchor.GetComponentInChildren<PreviewMarker>(true);
            if (marker != null) target = marker.gameObject;
        }

        // 2) Fallback: try a tagged object (e.g., a controller-follow preview)
        if (target == null && !string.IsNullOrEmpty(fallbackPreviewTag))
        {
            var tagged = GameObject.FindWithTag(fallbackPreviewTag);
            if (tagged != null) target = tagged;
        }

        if (target == null) return;

        if (destroyOnSuccess) Destroy(target);
        else target.SetActive(false);
    }

    // Optional: if you load persisted anchors at startup,
    // suppress any preview immediately after load.
    public void OnAnchorsLoadCompleted(List<OVRSpatialAnchor> loaded)
    {
        if (loaded != null && loaded.Count > 0 && !string.IsNullOrEmpty(fallbackPreviewTag))
        {
            var tagged = GameObject.FindWithTag(fallbackPreviewTag);
            if (tagged) tagged.SetActive(false);
        }
    }
}
