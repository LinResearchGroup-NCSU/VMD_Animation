using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

// Lets XR controllers grab the molecule with the grip button: one hand moves and rotates it,
// two hands also scale it. VMDXRBootstrap adds this to the MoleculeRenderer in Simulator and
// Headset modes only, so desktop mode is unchanged.
// The live renderers draw without colliders, so a sphere collider follows the molecule's bounds.
// Device basis: tried on Valve Index controllers only, where VMDIndexGrip replaces the grip button
// with a firm squeeze. Quest Touch controllers (Quest 3S) use the plain grip button; untested.
[RequireComponent(typeof(MoleculeRenderer))]
public class VMDMoleculeGrab : MonoBehaviour
{
    // Limits of two-handed scaling, relative to the molecule's size when the grab began.
    private const float MinimumScaleRatio = 0.05f, MaximumScaleRatio = 50f;

    // Reusable buffer for measuring the molecule's bounds.
    private readonly List<Renderer> renderers = new List<Renderer>();
    private MoleculeRenderer molecule;
    // Sphere around the molecule's bounds that controllers and rays can hit.
    private SphereCollider grabVolume;
    private XRGrabInteractable grab;

    // At least one controller holds the molecule. VMDVRController then ignores the thumbstick,
    // which XR Interaction Toolkit uses for the grab.
    public bool IsHeld => grab != null && grab.isSelected;

    // Add and configure the collider, the grab interactable and its transformer.
    private void Awake()
    {
        molecule = GetComponent<MoleculeRenderer>();

        // The collider must exist before XRGrabInteractable collects colliders in its Awake.
        grabVolume = gameObject.AddComponent<SphereCollider>();
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
    }

    // Resize the grab volume to the molecule while it is not held, so it follows trajectory
    // playback and drawing-method changes. It is disabled while nothing is drawn.
    private void LateUpdate()
    {
        if (grab.isSelected)
            return;
        if (!VMDCameraController.TryGetMoleculeBounds(molecule, renderers, out Bounds bounds))
        {
            grabVolume.enabled = false;
            return;
        }
        float scale = Mathf.Max(transform.lossyScale.x, 1e-6f);
        grabVolume.center = transform.InverseTransformPoint(bounds.center);
        grabVolume.radius = bounds.extents.magnitude / scale;
        grabVolume.enabled = true;
    }
}
