using UnityEngine;

public class PointCloudHud : MonoBehaviour
{
    public PointCloudRenderer rendererRef;
    public Vector2 position = new Vector2(20, 20);
    public int fontSize = 20;
    public Color textColor = Color.white;

    void OnGUI()
    {
        if (rendererRef == null) return;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            normal = { textColor = textColor }
        };

        int w = rendererRef.FrameWidth;
        int h = rendererRef.FrameHeight;
        int valid = rendererRef.ValidPoints;
        int visible = rendererRef.VisiblePoints;

        string text =
            $"RX FPS: {rendererRef.LastStreamFps:F0}  |  Render FPS: {rendererRef.LastRenderFps:F0}\n" +
            $"Size: {w}x{h}  |  Valid: {valid} ({rendererRef.ValidDensity01 * 100f:F1}%)  |  " +
            $"Visible: {visible} ({rendererRef.VisibleDensity01 * 100f:F1}%)  |  PtSize: {rendererRef.pointSizeWorld:F4}";

        GUI.Label(new Rect(position.x, position.y, 1200, 70), text, style);
    }
}
