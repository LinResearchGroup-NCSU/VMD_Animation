using UnityEngine;
using UnityEngine.InputSystem;

// Controller buttons for VR. Who does what:
// - VMDVRController (this): reads the face buttons and thumbsticks, then calls the components below.
// - TrajectoryController: playback state (play, pause, step, seek), shared with the keyboard and OnGUI.
// - RepresentationController: switches the drawing method; VMD applies it and Unity redraws.
// - VMDXRBootstrap: chooses the mode, adds this component in Simulator and Headset modes, and
//   places the molecule in front of the user (Recenter).
// - VMDMoleculeGrab / XR Interaction Toolkit: grip grabs, moves, rotates and scales the molecule;
//   the controller ray only points at what grip or trigger act on.
//
// Bindings use OpenXR usages, so Quest Touch, Index, Vive and the XR Interaction Simulator agree:
//   Right A: play / pause            Right B: next representation
//   Right stick left / right: previous / next frame, repeating while held
//   Left X / Y: back / forward 100 frames      Left stick click: recenter the molecule
public class VMDVRController : MonoBehaviour
{
    private const float StickThreshold = 0.7f, StickRelease = 0.4f;
    private const float RepeatDelay = 0.4f, RepeatInterval = 0.1f;
    private const int JumpFrames = 100;

    [SerializeField] private TrajectoryController trajectoryController;
    [SerializeField] private RepresentationController representationController;
    [SerializeField] private VMDXRBootstrap bootstrap;

    private InputAction playPause, nextRepresentation, jumpBack, jumpForward, recenter, stepAxis;
    private VMDMoleculeGrab grab;
    private int stickDirection;
    private float nextRepeat;

    private void Awake()
    {
        if (trajectoryController == null) trajectoryController = FindAnyObjectByType<TrajectoryController>();
        if (bootstrap == null) bootstrap = FindAnyObjectByType<VMDXRBootstrap>();

        playPause = Button("Play/Pause", "RightHand", "PrimaryButton");
        nextRepresentation = Button("Next representation", "RightHand", "SecondaryButton");
        jumpBack = Button("Back 100 frames", "LeftHand", "PrimaryButton");
        jumpForward = Button("Forward 100 frames", "LeftHand", "SecondaryButton");
        recenter = Button("Recenter molecule", "LeftHand", "Primary2DAxisClick");
        stepAxis = new InputAction("Step frames", InputActionType.Value,
            "<XRController>{RightHand}/{Primary2DAxis}", expectedControlType: "Vector2");
    }

    private static InputAction Button(string name, string hand, string usage) =>
        new InputAction(name, InputActionType.Button, $"<XRController>{{{hand}}}/{{{usage}}}");

    private void OnEnable()
    {
        foreach (var action in Actions()) action.Enable();
    }

    private void OnDisable()
    {
        foreach (var action in Actions()) action.Disable();
    }

    private void OnDestroy()
    {
        foreach (var action in Actions()) action.Dispose();
    }

    private InputAction[] Actions() => new[] { playPause, nextRepresentation, jumpBack, jumpForward, recenter, stepAxis };

    private void Update()
    {
        if (trajectoryController != null)
        {
            if (playPause.WasPressedThisFrame()) trajectoryController.Toggle();
            if (jumpBack.WasPressedThisFrame()) trajectoryController.Step(-JumpFrames);
            if (jumpForward.WasPressedThisFrame()) trajectoryController.Step(JumpFrames);
            UpdateStepping();
        }
        if (nextRepresentation.WasPressedThisFrame()) Representation()?.NextRepresentation();
        if (recenter.WasPressedThisFrame()) bootstrap?.Recenter();
    }

    // The MoleculeRenderer's own controller, which it initializes with the VMD client in its OnEnable.
    // Not FindAnyObjectByType: another, uninitialized RepresentationController may exist in the scene.
    private RepresentationController Representation()
    {
        if (representationController == null)
        {
            var molecule = FindAnyObjectByType<MoleculeRenderer>();
            if (molecule != null) representationController = molecule.GetComponent<RepresentationController>();
        }
        return representationController;
    }

    // One step when the stick is pushed past the threshold, then repeats while it stays there.
    // Ignored while the molecule is held: XR Interaction Toolkit then uses the stick for the grab.
    private void UpdateStepping()
    {
        if (grab == null) grab = FindAnyObjectByType<VMDMoleculeGrab>();
        float x = grab != null && grab.IsHeld ? 0f : stepAxis.ReadValue<Vector2>().x;
        int direction = x > StickThreshold ? 1 : x < -StickThreshold ? -1 : 0;
        if (Mathf.Abs(x) < StickRelease)
        {
            stickDirection = 0;
            return;
        }
        if (direction == 0)
            return;
        if (direction != stickDirection)
        {
            stickDirection = direction;
            nextRepeat = Time.unscaledTime + RepeatDelay;
            trajectoryController.Step(direction);
        }
        else if (Time.unscaledTime >= nextRepeat)
        {
            nextRepeat = Time.unscaledTime + RepeatInterval;
            trajectoryController.Step(direction);
        }
    }
}
