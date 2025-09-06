using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Put this on the Content child under your spatial anchor.
/// Lets you translate/rotate/scale Content at runtime on Quest using controllers.
/// Works with OpenXR (CommonUsages).
/// </summary>
public class AnchoredContentManipulator : MonoBehaviour
{
    [Header("Anchoring")]
    [Tooltip("Anchor root (XRAnchor / OVRSpatialAnchor). If null, uses parent.")]
    public Transform anchorRoot;

    [Header("One-hand tweak speeds")]
    public float moveSpeedPerSecond = 0.35f;           // m/s in local XZ
    public float verticalSpeedPerSecond = 0.35f;       // m/s with trigger/grip
    public float yawPitchDegPerSecond = 45f;           // deg/s via right stick
    public float rollDegPerSecond = 60f;               // deg/s via A/B

    [Header("Two-hand (both grips)")]
    public float minScale = 0.05f;
    public float maxScale = 5f;

    [Header("Edit mode")]
    [Tooltip("If false, ignores inputs. Toggle at runtime with Right primary button (A).")]
    public bool editMode = true;

    // XR devices
    private InputDevice leftHand, rightHand;

    // Two-hand state
    private bool twoHandActive = false;
    private Vector3 baseMidLocal;
    private Vector3 baseVecLocal;
    private float   baseDist;
    private Vector3 baseContentPosLocal;
    private Quaternion baseContentRotLocal;
    private Vector3 baseContentScaleLocal;

    void Awake()
    {
        if (anchorRoot == null && transform.parent != null)
            anchorRoot = transform.parent;
    }

    void OnEnable()
    {
        GetDevices();
    }

    void GetDevices()
    {
        leftHand  = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
    }

    void Update()
    {
        if (!leftHand.isValid || !rightHand.isValid) GetDevices();

        // Toggle edit mode with Right A button (optional)
        if (rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out bool aPressed) && aPressed)
        {
            // small debounce
            // (hold A to continuously toggle would be annoying; keep it simple)
        }

        if (!editMode || anchorRoot == null) return;

        // Read common inputs
        leftHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 leftStick);
        rightHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rightStick);
        leftHand.TryGetFeatureValue(CommonUsages.trigger, out float leftTrigger);
        leftHand.TryGetFeatureValue(CommonUsages.grip, out float leftGrip);
        rightHand.TryGetFeatureValue(CommonUsages.trigger, out float rightTrigger);
        rightHand.TryGetFeatureValue(CommonUsages.grip, out float rightGrip);

        // One-hand “nudge” controls (when not doing two-hand)
        bool bothGripping = (leftGrip > 0.8f) && (rightGrip > 0.8f);

        if (!bothGripping)
        {
            OneHandTranslateRotate(leftStick, leftTrigger, leftGrip, rightStick);
        }

        // Two-hand grab/rotate/scale (both grips held)
        HandleTwoHand(bothGripping);
    }

    private void OneHandTranslateRotate(Vector2 leftStick, float leftTrigger, float leftGrip, Vector2 rightStick)
    {
        float dt = Time.deltaTime;

        // Move on local XZ using left stick
        Vector3 rightLocal = Vector3.right;
        Vector3 forwardLocal = Vector3.forward;

        Vector3 deltaXZ =
            rightLocal * (leftStick.x * moveSpeedPerSecond * dt) +
            forwardLocal * (leftStick.y * moveSpeedPerSecond * dt);

        transform.localPosition += deltaXZ;

        // Up/down with trigger/grip (use whichever is pressed more)
        float upDown = (leftTrigger > leftGrip) ? leftTrigger : -leftGrip;
        transform.localPosition += Vector3.up * (upDown * verticalSpeedPerSecond * dt);

        // Yaw/pitch with right stick
        float yaw   = rightStick.x * yawPitchDegPerSecond * dt;
        float pitch = -rightStick.y * yawPitchDegPerSecond * dt;

        transform.localRotation = Quaternion.Euler(pitch, yaw, 0f) * transform.localRotation;

        // Roll with A/B on right controller
        if (rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out bool aPressed) && aPressed)
            transform.localRotation = Quaternion.AngleAxis(-rollDegPerSecond * dt, Vector3.forward) * transform.localRotation;
        if (rightHand.TryGetFeatureValue(CommonUsages.secondaryButton, out bool bPressed) && bPressed)
            transform.localRotation = Quaternion.AngleAxis( rollDegPerSecond * dt, Vector3.forward) * transform.localRotation;
    }

    private void HandleTwoHand(bool bothGripping)
    {
        // Need controller positions
        if (!leftHand.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 leftPosW)) return;
        if (!rightHand.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rightPosW)) return;

        // Convert to anchor-local space so edits are relative to the anchor
        Vector3 leftLocal  = anchorRoot.InverseTransformPoint(leftPosW);
        Vector3 rightLocal = anchorRoot.InverseTransformPoint(rightPosW);
        Vector3 midLocal   = 0.5f * (leftLocal + rightLocal);
        Vector3 vecLocal   = rightLocal - leftLocal;
        float   dist       = vecLocal.magnitude;

        if (bothGripping && !twoHandActive)
        {
            // Begin gesture
            twoHandActive = true;
            baseMidLocal = midLocal;
            baseVecLocal = vecLocal.sqrMagnitude > 1e-6f ? vecLocal.normalized : Vector3.right;
            baseDist = Mathf.Max(1e-4f, dist);

            baseContentPosLocal  = transform.localPosition;
            baseContentRotLocal  = transform.localRotation;
            baseContentScaleLocal = transform.localScale;
            return;
        }

        if (!bothGripping && twoHandActive)
        {
            // End gesture
            twoHandActive = false;
            return;
        }

        if (twoHandActive)
        {
            // Translation: follow the midpoint delta
            Vector3 deltaMid = (midLocal - baseMidLocal);
            transform.localPosition = baseContentPosLocal + deltaMid;

            // Yaw around anchor-up based on hand vector change (project to XZ plane)
            Vector3 baseXZ = new Vector3(baseVecLocal.x, 0f, baseVecLocal.z).normalized;
            Vector3 currXZ = new Vector3(vecLocal.x,    0f, vecLocal.z).normalized;
            if (baseXZ.sqrMagnitude > 0f && currXZ.sqrMagnitude > 0f)
            {
                float yaw = SignedAngleOnPlane(baseXZ, currXZ, Vector3.up);
                transform.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * baseContentRotLocal;
            }

            // Scale by distance ratio
            float s = Mathf.Clamp(dist / baseDist, minScale, maxScale);
            transform.localScale = baseContentScaleLocal * s;
        }
    }

    // Signed angle between a->b around plane normal n (all in local space)
    private static float SignedAngleOnPlane(Vector3 a, Vector3 b, Vector3 n)
    {
        a = Vector3.ProjectOnPlane(a, n).normalized;
        b = Vector3.ProjectOnPlane(b, n).normalized;
        float ang = Vector3.SignedAngle(a, b, n);
        return ang;
    }
}
