using UnityEngine;
using UnityEngine.InputSystem;

// This component allows controlling VMD from the keyboard, for testing without a VR headset.

// Add this component to the same GameObject as VMDClient.
[RequireComponent(typeof(VMDClient))]
public class VMDKeyboardController : MonoBehaviour
{
    private VMDClient client;
    [SerializeField] private TrajectoryController trajectoryController;
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

    // Translate key presses into VMD commands: Enter / P play or pause trajectory playback,
    // 1 / 2 / 3 jump to frames 100 / 500 / 1000, Left / Right or , / . step one frame,
    // Space toggles the same playback controller used by desktop and XR buttons, H cycles the highlight.
    // The XR Interaction Simulator also reads Space, the arrows, digits, P and H, so in Simulator
    // mode only keys it leaves free are used: Enter toggles playback, , / . step one frame.
    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.commaKey.wasPressedThisFrame)
            trajectoryController?.Step(-1);
        if (keyboard.periodKey.wasPressedThisFrame)
            trajectoryController?.Step(1);

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

        // One command per press, rather than repeating every frame while held.
        // TODO: Add more commands for controlling VMD, e.g. changing representations, coloring, etc.
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
        // H cycles N_DOMAIN -> C_DOMAIN -> LINKER -> off; desktop mode only (the simulator reads H).
        if (keyboard.hKey.wasPressedThisFrame)
            highlight?.Cycle();
    }
}
