using UnityEngine;
using UnityEngine.InputSystem;

// Controller buttons for VR. Who does what:
// - VMDVRController (this): reads the face buttons and thumbsticks, then calls the components
//   below.
// - TrajectoryController: playback state (play, pause, step, seek), shared with the keyboard,
//   the desktop OnGUI panel and the XR status panel.
// - RepresentationController: switches the drawing method; VMD applies it and Unity redraws.
// - VMDXRBootstrap: chooses the mode, adds this component in Simulator and Headset modes, and
//   places the molecule in front of the user (Recenter, ToggleImmersive).
// - VMDMoleculeGrab / XR Interaction Toolkit: grip grabs, moves, rotates and scales the molecule;
//   the controller ray only points at what grip or trigger act on.
// - VMDStatusPanel: the panel buttons, pressed with a ray and the trigger, including Highlight.
//
// Bindings use OpenXR usages, so Quest Touch, Index, Vive and the XR Interaction Simulator agree:
//   Right A: play / pause            Right B: next representation
//   Right stick left / right: previous / next frame, repeating while held
//   Left X / Y: back / forward 100 frames      Left stick click: recenter the molecule
//   Right stick click: toggle immersive size (stand inside the molecule) / in-front size
//
// Device basis: these bindings have only been pressed on Valve Index controllers (2026-11-10).
// - Index: both controllers are labelled A (lower) and B (upper), so "Left X / Y" is the left
//   A / B. The stick is the thumbstick, not the trackpad.
// - Meta Quest 3S (Touch Plus): right A / B, left X / Y, as written above. They resolve through
//   the same usages (PrimaryButton, SecondaryButton, Primary2DAxis, Primary2DAxisClick), so no
//   change should be needed, but this has not been tried on a Quest.
public class VMDVRController : MonoBehaviour
{
    // The stick steps a frame when pushed past StickThreshold and re-arms below StickRelease.
    private const float StickThreshold = 0.7f, StickRelease = 0.4f;
    // Held past the threshold, stepping repeats after RepeatDelay, every RepeatInterval seconds.
    private const float RepeatDelay = 0.4f, RepeatInterval = 0.1f;
    // Frames jumped by the left face buttons.
    private const int JumpFrames = 100;

    // Found in the scene when not assigned.
    [SerializeField] private TrajectoryController trajectoryController;
    [SerializeField] private RepresentationController representationController;
    [SerializeField] private VMDXRBootstrap bootstrap;

    private InputAction playPause, nextRepresentation, jumpBack, jumpForward, recenter, immersive, stepAxis;
    private VMDMoleculeGrab grab;
    // Direction the stick is held in (-1, 0, 1), and when the next repeated step is due.
    private int stickDirection;
    private float nextRepeat;

    // Simulator only: when Shift (which sends button keys to the left controller) is released
    // while a button key is still held, the XR Interaction Simulator hands that key to the right
    // controller. Shift + 1 would then also toggle Play/Pause, and Shift + 2 also switch the
    // drawing method. Right-hand input is therefore ignored for a short time after Shift is
    // released: ShiftGuardFrames frames and ShiftGuardSeconds seconds, whichever lasts longer.
    private const int ShiftGuardFrames = 3;
    private const float ShiftGuardSeconds = 0.2f;
    private bool shiftWasHeld;
    private int shiftReleaseFrame = int.MinValue;
    private float shiftReleaseTime = float.NegativeInfinity;

    // Find the collaborators and create one input action per binding listed in the header.
    private void Awake()
    {
        if (trajectoryController == null) trajectoryController = FindAnyObjectByType<TrajectoryController>();
        if (bootstrap == null) bootstrap = FindAnyObjectByType<VMDXRBootstrap>();

        playPause = Button("Play/Pause", "RightHand", "PrimaryButton");
        nextRepresentation = Button("Next representation", "RightHand", "SecondaryButton");
        jumpBack = Button("Back 100 frames", "LeftHand", "PrimaryButton");
        jumpForward = Button("Forward 100 frames", "LeftHand", "SecondaryButton");
        recenter = Button("Recenter molecule", "LeftHand", "Primary2DAxisClick");
        immersive = Button("Toggle immersive size", "RightHand", "Primary2DAxisClick");
        stepAxis = new InputAction("Step frames", InputActionType.Value,
            "<XRController>{RightHand}/{Primary2DAxis}", expectedControlType: "Vector2");
    }

    // A button action bound by hand and OpenXR usage, so it matches every controller model.
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

    // Every action of this component, for enabling, disabling and disposing them together.
    private InputAction[] Actions() => new[] { playPause, nextRepresentation, jumpBack, jumpForward, recenter, immersive, stepAxis };

    // One command per button press. Playback commands need a TrajectoryController.
    private void Update()
    {
        bool rightBlocked = SimulatorShiftGuard();
        if (trajectoryController != null)
        {
            if (playPause.WasPressedThisFrame() && !rightBlocked) trajectoryController.Toggle();
            if (jumpBack.WasPressedThisFrame()) trajectoryController.Step(-JumpFrames);
            if (jumpForward.WasPressedThisFrame()) trajectoryController.Step(JumpFrames);
            UpdateStepping(rightBlocked);
        }
        if (nextRepresentation.WasPressedThisFrame() && !rightBlocked) Representation()?.NextRepresentation();
        if (recenter.WasPressedThisFrame()) bootstrap?.Recenter();
        if (immersive.WasPressedThisFrame() && !rightBlocked) bootstrap?.ToggleImmersive();
    }

    // True while right-hand input should be ignored because Shift was just released in Simulator
    // mode (see ShiftGuardFrames). Always false in Headset mode, where the keyboard plays no part.
    private bool SimulatorShiftGuard()
    {
        if (VMDXRBootstrap.ActiveMode != VMDXRBootstrap.Mode.Simulator)
            return false;
        Keyboard keyboard = Keyboard.current;
        bool shiftHeld = keyboard != null && keyboard.shiftKey.isPressed;
        if (shiftWasHeld && !shiftHeld)
        {
            shiftReleaseFrame = Time.frameCount;
            shiftReleaseTime = Time.unscaledTime;
        }
        shiftWasHeld = shiftHeld;
        // long: before the first release shiftReleaseFrame is int.MinValue, and an int
        // subtraction would overflow to a negative number and block the right hand for good.
        return (long)Time.frameCount - shiftReleaseFrame <= ShiftGuardFrames ||
            Time.unscaledTime - shiftReleaseTime < ShiftGuardSeconds;
    }

    // The MoleculeRenderer's own controller, which it initializes with the VMD client in its
    // OnEnable. Not FindAnyObjectByType: another, uninitialized RepresentationController may exist
    // in the scene.
    private RepresentationController Representation()
    {
        if (representationController == null)
        {
            var molecule = FindAnyObjectByType<MoleculeRenderer>();
            if (molecule != null) representationController = molecule.GetComponent<RepresentationController>();
        }
        return representationController;
    }

    // Right stick left / right: one step when the stick is pushed past the threshold, then
    // repeats while it stays there. Ignored while the molecule is held: XR Interaction Toolkit
    // then uses the stick for the grab. Also ignored while 'blocked' (SimulatorShiftGuard).
    private void UpdateStepping(bool blocked)
    {
        if (grab == null) grab = FindAnyObjectByType<VMDMoleculeGrab>();
        float x = blocked || (grab != null && grab.IsHeld) ? 0f : stepAxis.ReadValue<Vector2>().x;
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
