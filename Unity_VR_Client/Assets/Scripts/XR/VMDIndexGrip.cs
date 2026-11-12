using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.OpenXR.Features.Interactions;

// Makes Valve Index controllers grab on a firm squeeze instead of on the grip "button".
//
// Why: Valve Index grips report finger curl, so simply holding the controller reads as a pressed
// grip and the molecule never lets go. When an Index controller appears, the interactors' select
// input is switched to grip force: grabbing then needs a deliberate squeeze, and relaxing the hand
// releases. The switch is made once, for the first Index controller seen in the session.
//
// Devices: Valve Index controllers ("Knuckles") only, detected through the OpenXR Valve Index
// Controller Profile, e.g. a Valve Index or another SteamVR headset paired with Index controllers.
// It changes nothing for other controllers: Meta Quest Touch / Touch Plus (Quest 2, Quest 3 and
// Quest 3S over Link, Air Link or Steam Link), HTC Vive wands and the XR Interaction Simulator
// keep the XRI default grip button: hold the grip button to grab, release it to let go.
//
// VMDXRBootstrap adds this component in Headset mode.
public class VMDIndexGrip : MonoBehaviour
{
    // Squeeze force that grabs. The force is scaled so that the Button action's default press
    // point (0.5) is reached at this force.
    private const float SqueezeForce = 0.3f;

    // The actions created here, disposed with this component.
    private readonly List<InputAction> actions = new List<InputAction>();
    // The switch to grip force has been made.
    private bool applied;

    // Wait for an Index controller, then rebind every grab interactor once.
    private void Update()
    {
        if (applied || !IndexPresent())
            return;
        applied = true;
        int count = 0;
        foreach (var interactor in FindObjectsByType<XRBaseInputInteractor>(FindObjectsSortMode.None))
        {
            string hand = interactor.handedness == InteractorHandedness.Left ? "LeftHand"
                : interactor.handedness == InteractorHandedness.Right ? "RightHand" : null;
            // Only grab interactors bound to the grip "Select" action, not e.g. teleport (thumbstick).
            InputAction current = interactor.selectInput.inputActionReferencePerformed != null
                ? interactor.selectInput.inputActionReferencePerformed.action
                : interactor.selectInput.inputActionPerformed;
            if (hand == null || current == null || current.name != "Select")
                continue;
            // 'performed' decides grabbed or not; 'value' is the analog amount XRI also reads.
            string path = $"<ValveIndexController>{{{hand}}}/gripForce";
            var performed = new InputAction(interactor.name + " Index squeeze", InputActionType.Button, path,
                processors: $"scale(factor={0.5f / SqueezeForce:0.###})");
            var value = new InputAction(interactor.name + " Index squeeze value", InputActionType.Value, path,
                expectedControlType: "Axis");
            performed.Enable();
            value.Enable();
            actions.Add(performed);
            actions.Add(value);
            interactor.selectInput.inputActionPerformed = performed;
            interactor.selectInput.inputActionValue = value;
            interactor.selectInput.inputSourceMode = UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader.InputSourceMode.InputAction;
            count++;
        }
        Debug.Log($"[VMDIndexGrip] Index controllers found; {count} interactors now grab on a squeeze (force {SqueezeForce}).", this);
    }

    // Whether the Input System currently lists a Valve Index controller.
    private static bool IndexPresent()
    {
        foreach (var device in InputSystem.devices)
            if (device is ValveIndexControllerProfile.ValveIndexController)
                return true;
        return false;
    }

    private void OnDestroy()
    {
        foreach (var action in actions)
            action.Dispose();
        actions.Clear();
    }
}
