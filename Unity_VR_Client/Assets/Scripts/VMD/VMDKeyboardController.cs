using UnityEngine;
using UnityEngine.InputSystem;

// This component allows controlling VMD from the keyboard, for testing without a VR headset.

// Add this component to the same GameObject as VMDClient.
[RequireComponent(typeof(VMDClient))]
public class VMDKeyboardController : MonoBehaviour
{
    private VMDClient client;
    [SerializeField] private TrajectoryController trajectoryController;

    // Cache the VMDClient and locate a TrajectoryController (same GameObject first, then the scene).
    private void Awake()
    {
        client = GetComponent<VMDClient>();
        if (trajectoryController == null)
            trajectoryController = GetComponent<TrajectoryController>();
        if (trajectoryController == null)
            trajectoryController = FindAnyObjectByType<TrajectoryController>();
    }

    // Translate key presses into VMD commands: Enter / P play or pause trajectory playback,
    // 1 / 2 / 3 jump to frames 100 / 500 / 1000, Left / Right step one frame,
    // Space toggles the same playback controller used by desktop and XR buttons.
    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

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
        if (keyboard.hKey.wasPressedThisFrame)
            client.SendCommand("HIGHLIGHT"); // Highlight the molecule, TODO: implement in bridge.
    }
}
