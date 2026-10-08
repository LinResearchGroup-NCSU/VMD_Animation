using UnityEngine;
using UnityEngine.InputSystem;

// This component allows controlling VMD from the keyboard, for testing without a VR headset.

// Add this component to the same GameObject as VMDClient.
[RequireComponent(typeof(VMDClient))]
public class VMDKeyboardController : MonoBehaviour
{
    private VMDClient client;
    [SerializeField] private TrajectoryController trajectoryController;

    private void Awake()
    {
        client = GetComponent<VMDClient>();
        if (trajectoryController == null)
            trajectoryController = GetComponent<TrajectoryController>();
        if (trajectoryController == null)
            trajectoryController = FindAnyObjectByType<TrajectoryController>();
    }

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
            client.SendCommand("FRAME 100");
        if (keyboard.digit2Key.wasPressedThisFrame)
            client.SendCommand("FRAME 500");
        if (keyboard.digit3Key.wasPressedThisFrame)
            client.SendCommand("FRAME 1000");
        if (keyboard.leftArrowKey.wasPressedThisFrame)
            client.SendCommand("STEP -1");
        if (keyboard.rightArrowKey.wasPressedThisFrame)
            client.SendCommand("STEP 1");
        if (keyboard.spaceKey.wasPressedThisFrame)
            client.SendCommand("TOGGLE"); // Toggle play/pause, TODO: implement in bridge.
        if (keyboard.hKey.wasPressedThisFrame)
            client.SendCommand("HIGHLIGHT"); // Highlight the molecule, TODO: implement in bridge.
        // if (keyboard.cKey.wasPressedThisFrame)
        //     client.RequestCoordinates(); // Request coordinates of the current frame from VMD.
        
    }
}
