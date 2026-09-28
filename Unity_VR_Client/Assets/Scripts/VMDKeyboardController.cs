using UnityEngine;
using UnityEngine.InputSystem;

// This component allows controlling VMD from the keyboard, for testing without a VR headset.

// Add this component to the same GameObject as VMDClient.
[RequireComponent(typeof(VMDClient))]
public class VMDKeyboardController : MonoBehaviour
{
    private VMDClient client;

    private void Awake()
    {
        client = GetComponent<VMDClient>();
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        // One command per press, rather than repeating every frame while held.
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
    }
}
