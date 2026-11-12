using UnityEngine;
using UnityEngine.InputSystem;

// Keyboard bindings for trajectory playback and highlighting; Update lists the keys.
//
// The keys send nothing to VMD themselves. They call TrajectoryController, the playback
// controller that the on-screen buttons, VMDVRController and VMDStatusPanel also use, and
// VMDHighlightController.Cycle, which the status panel's Highlight button also calls.
//
// The component runs in every mode chosen by VMDXRBootstrap. Desktop and Headset modes use all
// keys. Simulator mode uses only Enter and , / . because the XR Interaction Simulator reads the
// other keys itself.
//
// Add this component to the same GameObject as VMDClient.
[RequireComponent(typeof(VMDClient))]
public class VMDKeyboardController : MonoBehaviour
{
    // Cached in Awake; the key handling below does not use it.
    private VMDClient client;
    // Playback controller the keys act on. Found in Awake when not assigned in the Inspector.
    [SerializeField] private TrajectoryController trajectoryController;
    // Target of the H key.
    private VMDHighlightController highlight;

    // Cache the VMDClient and locate a TrajectoryController (same GameObject first, then the scene).
    // The highlight controller is added at runtime when the scene has none, so scenes need no edits.
    private void Awake()
    {
        client = GetComponent<VMDClient>();
        if (trajectoryController == null)
            trajectoryController = GetComponent<TrajectoryController>();
        if (trajectoryController == null)
            trajectoryController = FindAnyObjectByType<TrajectoryController>();
        highlight = FindAnyObjectByType<VMDHighlightController>();
        if (highlight == null)
            highlight = gameObject.AddComponent<VMDHighlightController>();
    }

    // Translate key presses into TrajectoryController and highlight calls:
    // - Enter / Numpad Enter: play. P: pause. Space: toggle play and pause.
    // - Left / Right arrow, or , / .: step one frame back / forward.
    // - 1 / 2 / 3: seek to frame 100 / 500 / 1000.
    // - H: highlight the next selection (VMDHighlightController.Cycle).
    // The XR Interaction Simulator also reads Space, the arrows, digits, P and H, so in Simulator
    // mode only keys it leaves free are used: Enter toggles playback, , / . step one frame.
    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        // , and . are handled before the mode check, so they work in every mode.
        if (keyboard.commaKey.wasPressedThisFrame)
            trajectoryController?.Step(-1);
        if (keyboard.periodKey.wasPressedThisFrame)
            trajectoryController?.Step(1);

        // Simulator mode: Enter toggles play and pause, and the keys below are skipped.
        if (VMDXRBootstrap.ActiveMode == VMDXRBootstrap.Mode.Simulator)
        {
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                trajectoryController?.Toggle();
            return;
        }

        if (trajectoryController != null)
        {
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                trajectoryController.Play();
            if (keyboard.pKey.wasPressedThisFrame)
                trajectoryController.Pause();
        }

        if (keyboard.digit1Key.wasPressedThisFrame)
            trajectoryController?.Seek(100);
        if (keyboard.digit2Key.wasPressedThisFrame)
            trajectoryController?.Seek(500);
        if (keyboard.digit3Key.wasPressedThisFrame)
            trajectoryController?.Seek(1000);
        if (keyboard.leftArrowKey.wasPressedThisFrame)
            trajectoryController?.Step(-1);
        if (keyboard.rightArrowKey.wasPressedThisFrame)
            trajectoryController?.Step(1);
        if (keyboard.spaceKey.wasPressedThisFrame)
            trajectoryController?.Toggle();
        // H cycles through the highlight controller's selections (by default N_DOMAIN, C_DOMAIN,
        // LINKER), then off. Desktop and Headset modes only: the simulator reads H itself.
        if (keyboard.hKey.wasPressedThisFrame)
            highlight?.Cycle();
    }
}
