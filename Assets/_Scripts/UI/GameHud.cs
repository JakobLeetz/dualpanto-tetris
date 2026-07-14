using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal developer HUD: shows Score / Level / Lines as plain text, positioned just above the
/// playing field. The game itself is for blind players (haptics + later audio), so this is only a
/// sighted-dev readout. Creates its own screen-space canvas + text in code (no scene wiring - just
/// attach to any active GameObject); each frame it projects the frame's top edge to screen space so
/// the text hovers over the field regardless of camera/field placement.
/// </summary>
public class GameHud : MonoBehaviour
{
    [SerializeField] float pixelOffsetAboveField = 24f;

    RectTransform rt;
    Text label;

    void Start()
    {
        GameObject canvasGo = new GameObject("HudCanvas");
        canvasGo.transform.SetParent(transform, false);
        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        GameObject textGo = new GameObject("HudText");
        textGo.transform.SetParent(canvasGo.transform, false);
        label = textGo.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        label.fontSize = 24;
        label.color = Color.black;
        label.alignment = TextAnchor.LowerCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        rt = label.rectTransform;
        rt.sizeDelta = new Vector2(400f, 40f);
        rt.pivot = new Vector2(0.5f, 0f); // bottom-centre, so the text sits above its anchor point
    }

    void Update()
    {
        GameManager gm = GameManager.Instance;
        if (gm == null || label == null) return;

        label.text = $"Score: {gm.Score}    Level: {gm.Level}    Lines: {gm.LinesCleared}";

        Camera cam = Camera.main;
        GridManager grid = gm.Grid;
        if (cam == null || grid == null || grid.Frame == null) return;

        // Top-centre of the field's top edge (high grid-y = high world-z) projected to the screen.
        Bounds b = grid.Frame.bounds;
        Vector3 topCentre = new Vector3(b.center.x, b.center.y, b.max.z);
        Vector3 screen = cam.WorldToScreenPoint(topCentre);
        rt.position = new Vector3(screen.x, screen.y + pixelOffsetAboveField, 0f);
    }
}
