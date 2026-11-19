using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.OpenXR.Features.Interactions;

// Makes Valve Index controllers grab with the trigger instead of the grip.
//
// Why: Valve Index grips report finger curl, so simply holding the controller reads as a pressed
// grip and the molecule never lets go. v5 switched grabbing to a firm squeeze (grip force 0.3),
// but on the headset (2026-11-17) users pulled the trigger to grab, a squeeze was hard to judge,
// and a grab made before the switch was never released (see below). The trigger is now the grab
// button on Index controllers: pull to grab, let go to release; both triggers on the molecule
// scale it. The trigger still presses status-panel buttons: a trigger pull that starts while the
// ray is on a panel button is a UI press only, never a grab (TriggerGrab). Without that, the pull
// also grabbed the molecule behind the panel, and XRI then blocked the button press (seen on the
// headset, 2026-11-17: only the molecule could be dragged, the buttons did nothing).
//
// When an Index controller appears, the grab interactors' select input is switched to the trigger,
// once, for the first Index controller seen in the session. Before that switch the interactors use
// XRI's grip binding, so holding a controller that has just been turned on can already grab the
// molecule. That grab started on the old binding never sees a release on the new one and stayed
// held for the whole session (the molecule followed the ray whatever the user pressed), so the
// switch also cancels whatever the rebound interactors are holding.
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
    // Index controllers were found and grab with the trigger; VMDStatusPanel then shows the Index
    // controls instead of the generic grip ones.
    public static bool Active { get; private set; }

    // Play mode starts without a domain reload, so reset the static between sessions.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Active = false;

    // The actions created here, disposed with this component.
    private readonly List<InputAction> actions = new List<InputAction>();
    // The switch to the trigger has been made.
    private bool applied;

    // Wait for an Index controller, then rebind every grab interactor once.
    private void Update()
    {
        if (applied || !IndexPresent())
            return;
        applied = true;
        Active = true;
        int count = 0, released = 0;
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
            // 'performed' decides grabbed or not (the Button action's default press point, half
            // way); 'value' is the analog amount XRI also reads.
            string path = $"<ValveIndexController>{{{hand}}}/trigger";
            var performed = new InputAction(interactor.name + " Index trigger grab", InputActionType.Button, path);
            var value = new InputAction(interactor.name + " Index trigger grab value", InputActionType.Value, path,
                expectedControlType: "Axis");
            performed.Enable();
            value.Enable();
            actions.Add(performed);
            actions.Add(value);
            interactor.selectInput.inputActionPerformed = performed;
            interactor.selectInput.inputActionValue = value;
            interactor.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
            interactor.selectInput.bypass = new TriggerGrab(interactor, performed, value);
            // Drop a grab made with the old grip binding; it would never be released.
            if (interactor.hasSelection && interactor.interactionManager != null)
            {
                interactor.interactionManager.CancelInteractorSelection((IXRSelectInteractor)interactor);
                released++;
            }
            count++;
        }
        Debug.Log($"[VMDIndexGrip] Index controllers found; {count} interactors now grab with the trigger; " +
                  $"{released} grab(s) made before the switch released.", this);
    }

    // The select input of one rebound interactor: the trigger, except that a pull starting while
    // the interactor's ray is on UI (a panel button) does not grab. A grab in progress is not
    // affected when the ray crosses UI.
    private sealed class TriggerGrab : IXRInputButtonReader
    {
        private readonly XRBaseInputInteractor interactor;
        private readonly InputAction performed, value;
        // The current (or just released) pull started on UI; it stays a UI press until released.
        private bool pullOnUI;
        // Frame for which pullOnUI is up to date, so the Read methods agree whatever order XRI
        // calls them in.
        private int frame = -1;

        public TriggerGrab(XRBaseInputInteractor interactor, InputAction performed, InputAction value)
        {
            this.interactor = interactor;
            this.performed = performed;
            this.value = value;
        }

        private bool OverUI() => interactor is IUIInteractor ui && ui.TryGetUIModel(out TrackedDeviceModel model) &&
                                 model.currentRaycast.isValid;

        // Once per frame: a new pull decides UI or grab; a pull that has ended forgets it (on the
        // release frame itself pullOnUI is kept, so that release is not reported as a grab release).
        private void Refresh()
        {
            if (frame == Time.frameCount)
                return;
            frame = Time.frameCount;
            if (performed.WasPerformedThisFrame())
                pullOnUI = !interactor.hasSelection && OverUI();
            else if (!performed.IsPressed() && !performed.WasReleasedThisFrame())
                pullOnUI = false;
        }

        public bool ReadWasPerformedThisFrame()
        {
            Refresh();
            return performed.WasPerformedThisFrame() && !pullOnUI;
        }

        public bool ReadIsPerformed()
        {
            Refresh();
            return performed.IsPressed() && !pullOnUI;
        }

        public bool ReadWasCompletedThisFrame()
        {
            Refresh();
            return performed.WasReleasedThisFrame() && !pullOnUI;
        }

        public float ReadValue()
        {
            Refresh();
            return pullOnUI ? 0f : value.ReadValue<float>();
        }

        public bool TryReadValue(out float result)
        {
            result = ReadValue();
            return value.enabled;
        }
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
        Active = false;
        foreach (var action in actions)
            action.Dispose();
        actions.Clear();
    }
}
