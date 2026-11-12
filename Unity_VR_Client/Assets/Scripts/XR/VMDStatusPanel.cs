using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

// World-space status panel for XR, built in code; VMDXRBootstrap creates it in Simulator and
// Headset modes.
//
// Contents, top to bottom:
// - The desktop OnGUI panel, copied: Play/Pause, Previous and Next buttons above the same two
//   status lines, in the same pixel rectangles. The buttons call TrajectoryController like the
//   OnGUI buttons, so both panels behave the same.
// - A Highlight button and a highlight status line. The right controller's ray highlights the
//   structure it points at (VMDRayHighlight); the button turns that on and off, and its label
//   shows which. The desktop uses the H key instead.
// - The controller bindings (VMDVRController, VMDMoleculeGrab, VMDIndexGrip), as plain text.
//
// Buttons are pressed by pointing a controller ray and pulling the trigger.
// The panel stays put in the room, ahead and to the left of where the user looked when it was
// placed, upright and facing the head. Recenter (VMDXRBootstrap) places it in view again.
public class VMDStatusPanel : MonoBehaviour
{
    // Layout in GUI pixels from the panel's top-left corner. The first 480 x 100 pixels are
    // TrajectoryController.OnGUI's box; the Highlight button sits in its free space right of
    // Next, and the highlight line and the controller hints take a further 114 pixels below.
    private static readonly Vector2 Size = new Vector2(480f, 214f);
    private static readonly Rect PlayRect = new Rect(8, 8, 75, 28), PreviousRect = new Rect(91, 8, 90, 28),
        NextRect = new Rect(189, 8, 90, 28), HighlightRect = new Rect(287, 8, 100, 28),
        StatusRect = new Rect(8, 43, 460, 50), HighlightStatusRect = new Rect(8, 98, 464, 18),
        HintsRect = new Rect(8, 120, 464, 90);
    // One GUI pixel is 0.8 mm, so the panel is 38 x 17 cm; 0.5 mm was too small to read on an Index.
    // Device basis: this size and ViewOffset below were judged in a Valve Index only; they are the
    // same on every headset and have not been checked in a Quest 3S.
    private const float MetresPerPixel = 0.0008f;
    // Keep in step with VMDVRController's bindings. The text covers both controller families:
    // Index controllers have A/B on both hands and grab on a squeeze (VMDIndexGrip); Quest Touch
    // controllers have A/B on the right, X/Y on the left, and grab while the grip button is held.
    // Plain ASCII: the default TMP font has no arrow glyphs.
    private const string Hints =
        "Grip: grab / move / rotate (Index: squeeze, Quest: hold). Both grips: scale\n" +
        "Trigger: press a panel button    Right ray on a domain: highlight it\n" +
        "Right A: play / pause    Right B: drawing method\n" +
        "Right stick: left / right = frame, click = immersive size\n" +
        "Left A/X, B/Y: -100 / +100 frames    Left stick click: recenter";
    // Seconds between refreshes of the texts and the buttons' enabled state.
    private const float RefreshInterval = 0.2f;
    // Panel center relative to the head when placed: right, up and forward along the head's
    // heading, in meters. Following the left controller kept the panel in the hand; fixed in the
    // room it can be left alone.
    private static readonly Vector3 ViewOffset = new Vector3(-0.45f, -0.25f, 0.7f);
    // Placement waits for a head pose, at most this long.
    private const float HeadPoseWaitSeconds = 5f;

    // Found in the scene on first use.
    private TrajectoryController trajectory;
    private VMDHighlightController highlight;
    private VMDRayHighlight rayHighlight;
    private TextMeshProUGUI status, playLabel, highlightLabel, highlightStatus;
    // Every button, enabled only while VMD is connected.
    private Button[] buttons;
    // Next text refresh, and the deadline after which the panel is placed without a head pose.
    private float nextRefresh, placeDeadline;
    // The panel has been placed in the room.
    private bool placed;

    // Create the panel as a new scene object. It places itself once the head is tracked.
    public static VMDStatusPanel Create()
    {
        var root = new GameObject("VMD Status Panel", typeof(RectTransform));
        var panel = root.AddComponent<VMDStatusPanel>();
        panel.Build();
        panel.placeDeadline = Time.unscaledTime + HeadPoseWaitSeconds;
        return panel;
    }

    // Put the panel ahead and to the left of where the head is looking, facing the head. Called
    // once by LateUpdate and again by VMDXRBootstrap.Recenter.
    public void PlaceInView()
    {
        Camera head = Camera.main;
        if (head == null)
            return;
        Vector3 forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;
        Quaternion heading = Quaternion.LookRotation(forward, Vector3.up);
        transform.position = head.transform.position + heading * ViewOffset;
        // A canvas reads correctly when its forward points away from the viewer; keep it upright.
        Vector3 away = Vector3.ProjectOnPlane(transform.position - head.transform.position, Vector3.up);
        transform.rotation = Quaternion.LookRotation(away.sqrMagnitude > 1e-4f ? away : forward, Vector3.up);
        placed = true;
    }

    // Build the canvas, its background, the buttons and the three text blocks.
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
        // Turns pointing (VMDRayHighlight) on and off. Without it, as before, the next selection of
        // VMDHighlightController, then off, like the desktop H key.
        Button highlightButton = Button("Highlight", HighlightRect, ToggleHighlight, out highlightLabel);
        buttons = new[] { play, previous, next, highlightButton };
        status = Text("Status", transform, StatusRect, TextAlignmentOptions.TopLeft);
        highlightStatus = Text("Highlight Status", transform, HighlightStatusRect, TextAlignmentOptions.TopLeft);
        highlightStatus.color = new Color(1f, 0.9f, 0.5f, 1f);
        TextMeshProUGUI hints = Text("Hints", transform, HintsRect, TextAlignmentOptions.TopLeft);
        hints.fontSize = 12f;
        hints.color = new Color(0.8f, 0.8f, 0.8f, 1f);
        hints.text = Hints;
    }

    // Add a gray button with a centered label at 'area'; 'text' returns the label for later changes.
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

    // Add a white 13-pixel text block at 'area' that wraps, ends in an ellipsis when it does not
    // fit, and lets rays pass through to the buttons.
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

    // The Highlight button: switch pointing on or off, or cycle when there is no ray highlighter.
    private void ToggleHighlight()
    {
        Highlighter();
        if (rayHighlight == null) rayHighlight = FindAnyObjectByType<VMDRayHighlight>();
        if (rayHighlight != null) rayHighlight.TogglePointing();
        else highlight?.Cycle();
        nextRefresh = 0f;
    }

    // The scene's highlight controller. VMDKeyboardController normally adds one at startup; a
    // scene without the keyboard controller gets one on the VMDClient object, as the keyboard
    // controller would add it.
    private VMDHighlightController Highlighter()
    {
        if (highlight == null) highlight = FindAnyObjectByType<VMDHighlightController>();
        if (highlight == null)
        {
            var client = FindAnyObjectByType<VMDClient>();
            if (client != null) highlight = client.gameObject.AddComponent<VMDHighlightController>();
        }
        return highlight;
    }

    // Every RefreshInterval, copy the playback and highlight status into the texts, relabel the
    // Play/Pause button and enable or disable the buttons.
    private void Update()
    {
        if (Time.unscaledTime < nextRefresh)
            return;
        nextRefresh = Time.unscaledTime + RefreshInterval;
        if (trajectory == null) trajectory = FindAnyObjectByType<TrajectoryController>();
        status.text = trajectory != null ? trajectory.Status : "Waiting for the trajectory controller";
        // The ray highlighter needs a highlight controller; create one like the keyboard would.
        Highlighter();
        if (rayHighlight == null) rayHighlight = FindAnyObjectByType<VMDRayHighlight>();
        bool pointing = rayHighlight != null && rayHighlight.Pointing;
        highlightStatus.text = highlight != null && highlight.Status != "" ? highlight.Status
            : pointing ? "Point the right ray at a domain to highlight it" : "Highlight: off";
        highlightLabel.text = rayHighlight == null ? "Highlight" : pointing ? "Pointing: On" : "Pointing: Off";
        playLabel.text = trajectory != null && trajectory.IsPlaying ? "Pause" : "Play";
        // Like OnGUI, the buttons work only while VMD is connected.
        bool connected = trajectory != null && trajectory.IsConnected;
        foreach (var button in buttons) button.interactable = connected;
    }

    // Place once, as soon as the head is tracked (an untracked XR camera sits at its parent's
    // origin) or the wait has run out.
    private void LateUpdate()
    {
        if (placed)
            return;
        Camera head = Camera.main;
        if (head == null)
            return;
        if (head.transform.localPosition.sqrMagnitude > 1e-6f || Time.unscaledTime >= placeDeadline)
            PlaceInView();
    }
}
