using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

// World-space status panel for XR, built in code; VMDXRBootstrap creates it in Simulator and
// Headset modes.
//
// Contents, top to bottom:
// - A drag bar: point a controller ray at it and grab (Index: trigger, other controllers: grip)
//   to move the panel. While held and after release the panel turns to face the head.
// - The desktop OnGUI panel, copied: Play/Pause, Previous, Next and Speed buttons above the same
//   two status lines. The buttons call TrajectoryController like the OnGUI buttons, so both panels
//   behave the same. Speed steps through 1x, 0.5x, 0.25x and 2x; its label shows the current one.
// - A Highlight button and a highlight status line. The right controller's ray highlights the
//   structure it points at (VMDRayHighlight); the button turns that on and off, and its label
//   shows which. The desktop uses the H key instead.
// - How to use the controllers (VMDVRController, VMDMoleculeGrab, VMDIndexGrip), one task per
//   line. Index controllers (VMDIndexGrip.Active) get their own text: they grab with the trigger
//   and have A/B on both hands; other controllers grab with the grip and have X/Y on the left.
//
// Buttons are pressed by pointing a controller ray and pulling the trigger. The drag bar is a
// physics collider without a UI raycast target, so the ray either presses a button (UI) or grabs
// the bar (interactable), never both.
// A Near-Far interactor whose hand is inside the molecule's grab volume (always so at immersive
// size) casts only near: the toolkit turns its ray off, UI presses with it, and a trigger pull
// aimed at a button grabbed the molecule instead (Valve Index, 2026-11-18). So while a hand's ray
// aims at the panel and the hand holds nothing, its near casting is off (KeepRaysOnPanel); the
// ray and the buttons work, and near grabbing elsewhere is unchanged.
// The panel stays put in the room, ahead and to the left of where the user looked when it was
// placed, upright and facing the head. Recenter (VMDXRBootstrap) places it in view again.
public class VMDStatusPanel : MonoBehaviour
{
    // Layout in GUI pixels from the panel's top-left corner: the drag bar, then the OnGUI box
    // (buttons and two status lines) shifted below it, the highlight line and the usage text.
    private static readonly Vector2 Size = new Vector2(540f, 330f);
    private static readonly Rect BarRect = new Rect(0, 0, 540, 26),
        PlayRect = new Rect(8, 34, 75, 28), PreviousRect = new Rect(91, 34, 90, 28),
        NextRect = new Rect(189, 34, 90, 28), HighlightRect = new Rect(287, 34, 110, 28),
        SpeedRect = new Rect(405, 34, 127, 28),
        StatusRect = new Rect(8, 69, 524, 40), HighlightStatusRect = new Rect(8, 112, 524, 18),
        HintsRect = new Rect(8, 136, 524, 190);
    // One GUI pixel is 0.8 mm, so the panel is 43 x 26 cm; 0.5 mm was too small to read on an Index.
    // Device basis: this size and ViewOffset below were judged in a Valve Index only; they are the
    // same on every headset and have not been checked in a Quest 3S.
    private const float MetresPerPixel = 0.0008f;
    // Keep in step with VMDVRController's and VMDIndexGrip's bindings. Plain ASCII: the default TMP
    // font has no arrow glyphs. <b> is TMP rich text.
    private const string IndexHints =
        "<b>Grab the molecule</b>: point at it, hold TRIGGER, move / turn your hand\n" +
        "<b>Resize</b>: hold both TRIGGERS on it, move hands apart (bigger) or together\n" +
        "<b>Panel</b>: point at a button + TRIGGER.  Move it: grab the bar on top\n" +
        "<b>Play / pause</b>: right A      <b>Drawing method</b>: right B\n" +
        "<b>One frame</b>: right stick left / right      <b>100 frames</b>: left A / B\n" +
        "<b>Inside the molecule / in front</b>: press the right stick down\n" +
        "<b>Bring molecule and panel back</b>: press the left stick down\n" +
        "<b>Highlight</b>: Pointing On, then aim the right ray at a domain";
    private const string GripHints =
        "<b>Grab the molecule</b>: point at it, hold GRIP, move / turn your hand\n" +
        "<b>Resize</b>: hold both GRIPS on it, move hands apart (bigger) or together\n" +
        "<b>Panel</b>: point at a button + TRIGGER.  Move it: grab the bar on top\n" +
        "<b>Play / pause</b>: right A      <b>Drawing method</b>: right B\n" +
        "<b>One frame</b>: right stick left / right      <b>100 frames</b>: left X / Y\n" +
        "<b>Inside the molecule / in front</b>: press the right stick down\n" +
        "<b>Bring molecule and panel back</b>: press the left stick down\n" +
        "<b>Highlight</b>: Pointing On, then aim the right ray at a domain";
    // Depth of the drag bar's collider, in GUI pixels (8 mm), so a ray hits it reliably.
    private const float BarDepth = 10f;
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
    private TextMeshProUGUI status, playLabel, highlightLabel, speedLabel, highlightStatus, hints, barLabel;
    // Every button, enabled only while VMD is connected.
    private Button[] buttons;
    // Makes the drag bar grabbable; it moves the whole panel.
    private XRGrabInteractable dragBar;
    // The usage text currently shown is the Index one.
    private bool? indexHints;
    // Next text refresh, and the deadline after which the panel is placed without a head pose.
    private float nextRefresh, placeDeadline;
    // The panel has been placed in the room.
    private bool placed;
    // Near-Far interactors (both hands), searched again every InteractorSearchInterval seconds.
    private NearFarInteractor[] interactors = new NearFarInteractor[0];
    private float nextInteractorSearch;
    private const float InteractorSearchInterval = 1f;

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
        FaceHead(head);
        placed = true;
        Debug.Log($"[VMDView] Status panel placed at {transform.position.ToString("F2")}, head {head.transform.position.ToString("F2")}; " +
                  $"t={Time.unscaledTime:F1} s.", this);
    }

    // Turn the panel upright toward the head. A canvas reads correctly when its forward points away
    // from the viewer.
    private void FaceHead(Camera head)
    {
        Vector3 away = Vector3.ProjectOnPlane(transform.position - head.transform.position, Vector3.up);
        if (away.sqrMagnitude < 1e-4f)
            away = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
        if (away.sqrMagnitude > 1e-4f)
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
    }

    // Build the canvas, its background, the drag bar, the buttons and the text blocks.
    private void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = (RectTransform)transform;
        rect.sizeDelta = Size;
        rect.localScale = Vector3.one * MetresPerPixel;
        // Lets XR Interaction Toolkit rays hit the buttons.
        gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
        // The background is not a UI target: only the buttons are, so "the ray is on UI" means
        // "on a button" (VMDIndexGrip lets such a trigger pull press the button instead of
        // grabbing), and the drag bar's collider is not hidden behind a UI hit.
        var background = gameObject.AddComponent<Image>();
        background.color = new Color(0.12f, 0.12f, 0.12f, 0.8f);
        background.raycastTarget = false;
        BuildDragBar();

        Button play = Button("Play", PlayRect, () => trajectory?.Toggle(), out playLabel);
        Button previous = Button("Previous", PreviousRect, () => trajectory?.PreviousFrame(), out _);
        Button next = Button("Next", NextRect, () => trajectory?.NextFrame(), out _);
        // Turns pointing (VMDRayHighlight) on and off. Without it, as before, the next selection of
        // VMDHighlightController, then off, like the desktop H key.
        Button highlightButton = Button("Highlight", HighlightRect, ToggleHighlight, out highlightLabel);
        // Steps through the playback speeds (TrajectoryController.CycleSpeed); the label shows the
        // current one.
        Button speed = Button("Speed 1x", SpeedRect, () => { trajectory?.CycleSpeed(); nextRefresh = 0f; }, out speedLabel);
        buttons = new[] { play, previous, next, highlightButton, speed };
        status = Text("Status", transform, StatusRect, TextAlignmentOptions.TopLeft);
        highlightStatus = Text("Highlight Status", transform, HighlightStatusRect, TextAlignmentOptions.TopLeft);
        highlightStatus.color = new Color(1f, 0.9f, 0.5f, 1f);
        hints = Text("Hints", transform, HintsRect, TextAlignmentOptions.TopLeft);
        hints.fontSize = 14f;
        hints.lineSpacing = 8f;
        hints.color = new Color(0.9f, 0.9f, 0.9f, 1f);
        hints.richText = true;
        UpdateHints();
    }

    // The bar across the top: a lighter strip with a label, a box collider over it and a grab
    // interactable on the panel, which collects that collider (its only one) when it is added.
    // Only the position follows the hand; LateUpdate keeps the panel facing the head.
    private void BuildDragBar()
    {
        var bar = new GameObject("Drag Bar", typeof(RectTransform), typeof(Image));
        Place((RectTransform)bar.transform, transform, BarRect);
        var image = bar.GetComponent<Image>();
        image.color = new Color(0.25f, 0.42f, 0.62f, 1f);
        // Not a UI target: the ray grabs the bar through its collider instead of pressing it.
        image.raycastTarget = false;
        barLabel = Text("Label", bar.transform, new Rect(0, 0, BarRect.width, BarRect.height), TextAlignmentOptions.Center);
        // The collider is in the bar's local units (GUI pixels), over the bar's rectangle and in
        // front of the canvas (local -z faces the viewer), so a ray reaches it before the panel's
        // background, which is a UI target.
        var box = bar.AddComponent<BoxCollider>();
        box.center = new Vector3(BarRect.width / 2f, -BarRect.height / 2f, -BarDepth / 2f - 2f);
        box.size = new Vector3(BarRect.width, BarRect.height, BarDepth);

        dragBar = gameObject.AddComponent<XRGrabInteractable>();
        var body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        dragBar.movementType = XRBaseInteractable.MovementType.Instantaneous;
        dragBar.throwOnDetach = false;
        dragBar.trackRotation = false;
        dragBar.trackScale = false;
        dragBar.useDynamicAttach = true;
        dragBar.farAttachMode = InteractableFarAttachMode.Far;
        dragBar.selectExited.AddListener(_ =>
        {
            if (Camera.main != null) FaceHead(Camera.main);
            Debug.Log($"[VMDView] Status panel moved to {transform.position.ToString("F2")}; t={Time.unscaledTime:F1} s.", this);
        });
    }

    // Show the Index or the generic usage text, and name the grab button on the drag bar.
    private void UpdateHints()
    {
        bool index = VMDIndexGrip.Active;
        if (indexHints == index)
            return;
        indexHints = index;
        hints.text = index ? IndexHints : GripHints;
        barLabel.text = index ? "Move this panel: point here and hold TRIGGER" : "Move this panel: point here and hold GRIP";
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
        KeepRaysOnPanel();
        if (Time.unscaledTime < nextRefresh)
            return;
        nextRefresh = Time.unscaledTime + RefreshInterval;
        UpdateHints();
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
        speedLabel.text = "Speed " + (trajectory != null ? trajectory.SpeedText : "1x");
        // Like OnGUI, the buttons work only while VMD is connected.
        bool connected = trajectory != null && trajectory.IsConnected;
        foreach (var button in buttons) button.interactable = connected;
    }

    // Every frame: turn near casting off for each hand whose ray aims at the panel and that holds
    // nothing, and back on otherwise (see the header). A grab in progress is left alone.
    private void KeepRaysOnPanel()
    {
        if (Time.unscaledTime >= nextInteractorSearch)
        {
            nextInteractorSearch = Time.unscaledTime + InteractorSearchInterval;
            interactors = FindObjectsByType<NearFarInteractor>(FindObjectsSortMode.None);
        }
        foreach (var interactor in interactors)
        {
            if (interactor == null || interactor.hasSelection)
                continue;
            Transform origin = interactor.farInteractionCaster?.effectiveCastOrigin;
            bool aimsAtPanel = placed && origin != null && RayHitsPanel(new Ray(origin.position, origin.forward));
            interactor.enableNearCasting = !aimsAtPanel;
        }
    }

    // Whether the ray crosses the panel's rectangle from its front or back.
    private bool RayHitsPanel(Ray ray)
    {
        var plane = new Plane(transform.forward, transform.position);
        if (!plane.Raycast(ray, out float distance))
            return false;
        var rect = (RectTransform)transform;
        return rect.rect.Contains(transform.InverseTransformPoint(ray.GetPoint(distance)));
    }

    // Place once, as soon as the head is tracked (an untracked XR camera sits at its parent's
    // origin) or the wait has run out. VMDXRBootstrap places the panel again with the molecule's
    // first placement, which in Headset mode waits until the headset is worn.
    // While the drag bar is held, keep the panel upright and facing the head.
    private void LateUpdate()
    {
        if (dragBar != null && dragBar.isSelected)
        {
            if (Camera.main != null) FaceHead(Camera.main);
            return;
        }
        if (placed)
            return;
        Camera head = Camera.main;
        if (head == null)
            return;
        if (head.transform.localPosition.sqrMagnitude > 1e-6f || Time.unscaledTime >= placeDeadline)
            PlaceInView();
    }
}
