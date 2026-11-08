using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

// World-space copy of the desktop OnGUI panel for XR: the same Play/Pause, Previous and Next buttons
// above the same two status lines, laid out in the same pixel rectangles. VMDXRBootstrap creates it in
// Simulator and Headset modes; it floats above the left controller, parallel to the view plane.
// Buttons are pressed by pointing a controller ray and pulling the trigger; they call
// TrajectoryController like the OnGUI buttons, so both panels behave the same.
public class VMDStatusPanel : MonoBehaviour
{
    // TrajectoryController.OnGUI layout, in GUI pixels relative to its 480 x 100 box.
    private static readonly Vector2 Size = new Vector2(480f, 100f);
    private static readonly Rect PlayRect = new Rect(8, 8, 75, 28), PreviousRect = new Rect(91, 8, 90, 28),
        NextRect = new Rect(189, 8, 90, 28), StatusRect = new Rect(8, 43, 460, 50);
    // One GUI pixel is 0.5 mm, so the panel is 24 x 5 cm.
    private const float MetresPerPixel = 0.0005f;
    private const float HeightAboveController = 0.1f, RefreshInterval = 0.2f;

    private Transform anchor;
    private TrajectoryController trajectory;
    private TextMeshProUGUI status, playLabel;
    private Button[] buttons;
    private float nextRefresh;

    public static VMDStatusPanel Create(Transform leftController)
    {
        var root = new GameObject("VMD Status Panel", typeof(RectTransform));
        var panel = root.AddComponent<VMDStatusPanel>();
        panel.anchor = leftController;
        panel.Build();
        return panel;
    }

    private void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = (RectTransform)transform;
        rect.sizeDelta = Size;
        rect.localScale = Vector3.one * MetresPerPixel;
        // Lets XR Interaction Toolkit rays hit the buttons.
        gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
        gameObject.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.12f, 0.75f);

        Button play = Button("Play", PlayRect, () => trajectory?.Toggle(), out playLabel);
        Button previous = Button("Previous", PreviousRect, () => trajectory?.PreviousFrame(), out _);
        Button next = Button("Next", NextRect, () => trajectory?.NextFrame(), out _);
        buttons = new[] { play, previous, next };
        status = Text("Status", transform, StatusRect, TextAlignmentOptions.TopLeft);
    }

    private Button Button(string label, Rect area, UnityEngine.Events.UnityAction onClick, out TextMeshProUGUI text)
    {
        var item = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
        Place((RectTransform)item.transform, transform, area);
        item.GetComponent<Image>().color = new Color(0.36f, 0.36f, 0.36f, 1f);
        var button = item.GetComponent<Button>();
        button.onClick.AddListener(onClick);
        text = Text("Label", item.transform, new Rect(0, 0, area.width, area.height), TextAlignmentOptions.Center);
        text.text = label;
        return button;
    }

    private static TextMeshProUGUI Text(string name, Transform parent, Rect area, TextAlignmentOptions alignment)
    {
        var text = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        Place(text.rectTransform, parent, area);
        text.fontSize = 13f;
        text.alignment = alignment;
        text.color = Color.white;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    // Position like a GUI Rect: x, y from the parent's top-left corner, y downward.
    private static void Place(RectTransform rect, Transform parent, Rect area)
    {
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(area.x, -area.y);
        rect.sizeDelta = area.size;
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefresh)
            return;
        nextRefresh = Time.unscaledTime + RefreshInterval;
        if (trajectory == null) trajectory = FindAnyObjectByType<TrajectoryController>();
        status.text = trajectory != null ? trajectory.Status : "Waiting for the trajectory controller";
        playLabel.text = trajectory != null && trajectory.IsPlaying ? "Pause" : "Play";
        // Like OnGUI, the buttons work only while VMD is connected.
        bool connected = trajectory != null && trajectory.IsConnected;
        foreach (var button in buttons) button.interactable = connected;
    }

    // Follow the controller and stay parallel to the view plane, like a screen overlay: a panel turned
    // toward the eye still projects as a trapezoid when it is off-center.
    private void LateUpdate()
    {
        Camera head = Camera.main;
        if (anchor == null || head == null)
            return;
        transform.position = anchor.position + Vector3.up * HeightAboveController;
        transform.rotation = head.transform.rotation;
    }
}
