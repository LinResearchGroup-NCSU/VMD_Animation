using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

// Lets XR controllers grab the molecule with the grip button: one hand moves and rotates it,
// two hands also scale it. VMDXRBootstrap adds this to the MoleculeRenderer in Simulator and
// Headset modes only, so desktop mode is unchanged.
// The live renderers draw without colliders, so a box collider follows the molecule's bounds. It is
// the world-axis box around the drawn geometry, on a child kept aligned with the world axes while
// the molecule is not held. (Until 2026-11-17 it was the bounding sphere of that box: much larger
// than the molecule, so rays stopped in empty space in front of it and looked stuck or short.)
// Device basis: tuned on Valve Index controllers, where VMDIndexGrip replaces the grip button with
// the trigger. Quest Touch controllers use the plain grip button (tested over Link, 2026-11-23).
// Grab diagnostics: every grab and release, and every HeldLogSeconds while held, is logged with the
// prefix "[VMDGrab]" and the select input of each holding interactor (value and pressed state), so a
// headset Player.log shows what keeps the molecule held. On 2026-11-17 the molecule followed the
// Index controller ray whether or not the grip was squeezed (docs/VR_ISSUES.md, issue 2).
[RequireComponent(typeof(MoleculeRenderer))]
public class VMDMoleculeGrab : MonoBehaviour
{
    // Limits of two-handed scaling, relative to the molecule's size when the grab began.
    private const float MinimumScaleRatio = 0.05f, MaximumScaleRatio = 50f;
    // While held, the holding interactors are logged this often.
    private const float HeldLogSeconds = 2f;
    private float nextHeldLog;

    // Reusable buffer for measuring the molecule's bounds.
    private readonly List<Renderer> renderers = new List<Renderer>();
    private MoleculeRenderer molecule;
    // Box around the molecule's bounds that controllers and rays can hit, on a child object.
    private BoxCollider grabVolume;
    private XRGrabInteractable grab;

    // At least one controller holds the molecule. VMDVRController then ignores the thumbstick,
    // which XR Interaction Toolkit uses for the grab.
    public bool IsHeld => grab != null && grab.isSelected;

    // Add and configure the collider, the grab interactable and its transformer.
    private void Awake()
    {
        molecule = GetComponent<MoleculeRenderer>();

        // The collider must exist before XRGrabInteractable collects colliders in its Awake.
        var volume = new GameObject("Grab Volume");
        volume.transform.SetParent(transform, false);
        grabVolume = volume.AddComponent<BoxCollider>();
        grabVolume.enabled = false;

        // XRGrabInteractable adds the Rigidbody it requires; keep it kinematic so nothing falls.
        grab = gameObject.AddComponent<XRGrabInteractable>();
        var body = GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.throwOnDetach = false;
        // Keep the grab point where the hand or ray touched, and leave far grabs at their distance.
        grab.useDynamicAttach = true;
        grab.farAttachMode = InteractableFarAttachMode.Far;
        // Two controllers may hold the molecule at once, which is what allows scaling.
        grab.selectMode = InteractableSelectMode.Multiple;

        // Registered with the interactable in its Start, so no default transformer is added.
        var transformer = gameObject.AddComponent<XRGeneralGrabTransformer>();
        transformer.allowTwoHandedScaling = true;
        // XRI clamps a two-handed scale to 0.25-2x by default, too little to walk into the molecule.
        transformer.minimumScaleRatio = MinimumScaleRatio;
        transformer.maximumScaleRatio = MaximumScaleRatio;

        grab.selectEntered.AddListener(args => LogGrab("Grabbed by", args.interactorObject));
        grab.selectExited.AddListener(args => LogGrab("Released by", args.interactorObject));
    }

    private void OnDestroy()
    {
        if (grab == null)
            return;
        grab.selectEntered.RemoveAllListeners();
        grab.selectExited.RemoveAllListeners();
    }

    // "[VMDGrab]" line for one grab or release, with every interactor holding the molecule now.
    private void LogGrab(string what, IXRSelectInteractor interactor)
    {
        nextHeldLog = Time.unscaledTime + HeldLogSeconds;
        Debug.Log($"[VMDGrab] {what} {Describe(interactor)}; held by {HoldersText()}; t={Time.unscaledTime:F1} s.", this);
    }

    // The holding interactors, or "nothing".
    private string HoldersText()
    {
        if (!grab.isSelected)
            return "nothing";
        var text = new System.Text.StringBuilder();
        foreach (var holder in grab.interactorsSelecting)
            text.Append(text.Length == 0 ? "" : ", ").Append(Describe(holder));
        return text.ToString();
    }

    // Interactor name and hand, plus its select input: the analog value and whether it counts as
    // pressed (for Index controllers this is the grip force set by VMDIndexGrip).
    private static string Describe(IXRSelectInteractor interactor)
    {
        if (interactor is not XRBaseInputInteractor input)
            return interactor?.transform != null ? interactor.transform.name : "unknown interactor";
        return $"{input.name} ({input.handedness}, select value {input.selectInput.ReadValue():F2}, " +
               $"pressed {input.selectInput.ReadIsPerformed()}, source {input.selectInput.inputSourceMode})";
    }

    // Resize the grab volume to the molecule while it is not held, so it follows trajectory
    // playback and drawing-method changes. It is disabled while nothing is drawn.
    private void LateUpdate()
    {
        if (grab.isSelected)
        {
            if (Time.unscaledTime >= nextHeldLog)
            {
                nextHeldLog = Time.unscaledTime + HeldLogSeconds;
                Debug.Log($"[VMDGrab] Still held by {HoldersText()}; t={Time.unscaledTime:F1} s.", this);
            }
            return;
        }
        if (!VMDCameraController.TryGetMoleculeBounds(molecule, renderers, out Bounds bounds))
        {
            grabVolume.enabled = false;
            return;
        }
        // The bounds are a world-axis box, so align the child with the world axes and size the box
        // in its local units (the molecule's scale is uniform).
        Transform volume = grabVolume.transform;
        volume.SetPositionAndRotation(bounds.center, Quaternion.identity);
        float scale = Mathf.Max(volume.lossyScale.x, 1e-6f);
        grabVolume.center = Vector3.zero;
        grabVolume.size = bounds.size / scale;
        grabVolume.enabled = true;
    }
}
