using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

// Lets XR controllers grab the molecule with the grip button: one hand moves and rotates it,
// two hands also scale it. VMDXRBootstrap adds this to the MoleculeRenderer in Simulator and
// Headset modes only, so desktop mode is unchanged.
// The live renderers draw without colliders, so a sphere collider follows the molecule's bounds.
[RequireComponent(typeof(MoleculeRenderer))]
public class VMDMoleculeGrab : MonoBehaviour
{
    private readonly List<Renderer> renderers = new List<Renderer>();
    private MoleculeRenderer molecule;
    private SphereCollider grabVolume;
    private XRGrabInteractable grab;

    public bool IsHeld => grab != null && grab.isSelected;

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
        grab.selectMode = InteractableSelectMode.Multiple;

        // Registered with the interactable in its Start, so no default transformer is added.
        var transformer = gameObject.AddComponent<XRGeneralGrabTransformer>();
        transformer.allowTwoHandedScaling = true;
    }

    // Resize the grab volume to the molecule while it is not held; it follows trajectory playback.
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
