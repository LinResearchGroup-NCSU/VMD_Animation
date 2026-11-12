using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Desktop-mode camera: orbits around the molecule and frames it.
// Left drag: orbit. Right/middle drag: pan. Wheel: zoom. F: fit molecule.
//
// The component is not placed in the scene. AttachToMainCamera adds it to Camera.main, in Desktop
// mode only: after the first scene has loaded, and from VMDXRBootstrap.Awake, which also covers the
// scene reload into Desktop mode after XR failed to start. In Simulator and Headset modes the XR
// rig drives the camera and VMDXRBootstrap places the molecule in front of the user instead.
//
// The camera always looks at 'pivot' from 'distance' away: orbiting turns the camera around the
// pivot, panning moves the pivot and zooming changes the distance.
//
// Callers:
// - RepresentationController.FitView calls FitMolecule: after an import, on returning to the
//   atom view, and when the coordinate route shows its first frame.
// - VMDXRBootstrap and VMDMoleculeGrab use the static TryGetMoleculeBounds to measure the molecule.
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public class VMDCameraController : MonoBehaviour
{
    // Molecule to frame. When empty, the scene's MoleculeRenderer is used.
    [SerializeField] private MoleculeRenderer molecule;
    // Margin for FitMolecule: the distance at which the bounding sphere just fits is multiplied
    // by this factor.
    [SerializeField, Min(1f)] private float fitPadding = 1.15f;
    // Orbit angle in degrees per pixel of mouse movement.
    [SerializeField, Min(0.01f)] private float rotationSpeed = 0.25f;
    // Zoom per unit of mouse-wheel input: the distance is multiplied by exp(-scroll * zoomSpeed).
    [SerializeField, Min(0.0001f)] private float zoomSpeed = 0.012f;

    // Reusable buffer for TryGetMoleculeBounds.
    private readonly List<Renderer> renderers = new List<Renderer>();
    private Camera controlledCamera;
    // World-space point the camera orbits and looks at; the molecule's bounds center after a fit.
    private Vector3 pivot;
    // Distance from the camera to the pivot.
    private float distance;
    // Radius of the molecule's bounding sphere at the last fit; zoom limits and clipping planes
    // are derived from it.
    private float radius;
    // FitMolecule has succeeded at least once. Until then LateUpdate retries it every frame.
    private bool fitted;

    // Attach only at runtime, so existing scenes and camera settings stay untouched.
    // Runs after Awake, when VMDXRBootstrap has chosen the mode; a headset camera is never orbited.
    // This hook runs only for the first scene of a Play session, so VMDXRBootstrap.Awake also calls
    // it. Scenes without a MoleculeRenderer get no controller; a second call adds nothing.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    internal static void AttachToMainCamera()
    {
        if (VMDXRBootstrap.ActiveMode != VMDXRBootstrap.Mode.Desktop)
            return;
        Camera main = Camera.main;
        if (main != null && FindAnyObjectByType<MoleculeRenderer>() != null &&
            main.GetComponent<VMDCameraController>() == null)
            main.gameObject.AddComponent<VMDCameraController>();
    }

    // Cache the camera that this controller drives.
    private void Awake()
    {
        controlledCamera = GetComponent<Camera>();
    }

    // Bounds of the scene's MoleculeRenderer; false if none exists or nothing is currently visible.
    private bool TryGetBounds(out Bounds bounds)
    {
        if (molecule == null)
            molecule = FindAnyObjectByType<MoleculeRenderer>();
        return TryGetMoleculeBounds(molecule, renderers, out bounds);
    }

    // Compute the combined world-space bounds of everything drawn under the molecule: the active,
    // enabled Renderer components (for example imported VMD meshes) plus the two scenes that draw
    // without a Renderer component, VMDCoordinateScene and VMDQuickSurfScene.
    // Returns false when 'molecule' is null or nothing is visible yet.
    // Shared with VMDXRBootstrap and VMDMoleculeGrab; 'renderers' is a reusable buffer.
    public static bool TryGetMoleculeBounds(MoleculeRenderer molecule, List<Renderer> renderers, out Bounds bounds)
    {
        bounds = default;
        if (molecule == null)
            return false;

        molecule.GetComponentsInChildren(false, renderers);
        bool found = false;
        foreach (Renderer renderer in renderers)
        {
            if (!renderer.enabled || renderer.forceRenderingOff)
                continue;
            if (!found)
                bounds = renderer.bounds;
            else
                bounds.Encapsulate(renderer.bounds);
            found = true;
        }
        var coordinateScene = molecule.GetComponentInChildren<VMDCoordinateScene>();
        if (coordinateScene != null && coordinateScene.isActiveAndEnabled && coordinateScene.HasFrame && coordinateScene.AtomInstanceCount > 0)
        {
            if (found) bounds.Encapsulate(coordinateScene.WorldBounds); else bounds = coordinateScene.WorldBounds;
            found = true;
        }
        // GPU QuickSurf is drawn without a Renderer component.
        var surfaceScene = molecule.GetComponentInChildren<VMDQuickSurfScene>();
        if (surfaceScene != null && surfaceScene.isActiveAndEnabled && surfaceScene.HasBounds)
        {
            if (found) bounds.Encapsulate(surfaceScene.WorldBounds); else bounds = surfaceScene.WorldBounds;
            found = true;
        }
        return found;
    }

    // Center the orbit pivot on the molecule and pick a camera distance (and orthographic size)
    // so the molecule's bounding sphere fits entirely in view, then apply the new view.
    // The camera keeps its orientation. Does nothing while no molecule geometry is visible.
    // Called by LateUpdate (first appearance and the F key) and RepresentationController.FitView.
    public void FitMolecule()
    {
        if (!TryGetBounds(out Bounds bounds))
            return;

        pivot = bounds.center;
        radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
        float aspect = Mathf.Max(controlledCamera.aspect, 0.01f);
        float verticalHalfAngle = controlledCamera.fieldOfView * Mathf.Deg2Rad * 0.5f;
        float horizontalHalfAngle = Mathf.Atan(Mathf.Tan(verticalHalfAngle) * aspect);
        // The bounding sphere fits at every orbit angle, including portrait windows: use the
        // narrower of the two half angles.
        distance = radius * fitPadding /
            Mathf.Sin(Mathf.Min(verticalHalfAngle, horizontalHalfAngle));
        if (controlledCamera.orthographic)
            controlledCamera.orthographicSize = radius * fitPadding / Mathf.Min(1f, aspect);
        fitted = true;
        ApplyView();
    }

    // Per-frame camera control: auto-fit once geometry first appears, then handle F (refit),
    // left-drag orbit, right/middle-drag pan, and scroll-wheel zoom.
    private void LateUpdate()
    {
        // Coordinates arrive asynchronously; fit when the first atoms become visible.
        if (!fitted)
        {
            FitMolecule();
            if (!fitted)
                return;
        }

        // Ignore keys and mouse movement meant for another window.
        if (!Application.isFocused)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
        {
            FitMolecule();
            return;
        }

        // No mouse control while the pointer is outside this camera's view or over the playback
        // panel that TrajectoryController draws.
        Mouse mouse = Mouse.current;
        if (mouse == null || !controlledCamera.pixelRect.Contains(mouse.position.ReadValue()) ||
            TrajectoryController.CapturesPointer(mouse.position.ReadValue()))
            return;

        Vector2 delta = mouse.delta.ReadValue();
        if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
        {
            // World units per pixel at the pivot's depth, so the molecule moves with the pointer.
            float halfHeight = controlledCamera.orthographic
                ? controlledCamera.orthographicSize
                : distance * Mathf.Tan(controlledCamera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            float unitsPerPixel = 2f * halfHeight / Mathf.Max(controlledCamera.pixelHeight, 1);
            pivot -= (transform.right * delta.x + transform.up * delta.y) * unitsPerPixel;
        }
        else if (mouse.leftButton.isPressed)
        {
            // Local-axis rotation permits a full orbit without an Euler-angle pole lock.
            transform.rotation = Quaternion.AngleAxis(delta.x * rotationSpeed, transform.up)
                * Quaternion.AngleAxis(-delta.y * rotationSpeed, transform.right)
                * transform.rotation;
        }

        // Exponential zoom: each wheel step changes the distance by the same factor. One frame's
        // factor is limited to e^-2 .. e^2, and the distance to 0.02 .. 1000 molecule radii.
        float zoom = Mathf.Exp(Mathf.Clamp(-mouse.scroll.ReadValue().y * zoomSpeed, -2f, 2f));
        distance = Mathf.Clamp(distance * zoom, radius * 0.02f, radius * 1000f);
        if (controlledCamera.orthographic)
            controlledCamera.orthographicSize = Mathf.Clamp(
                controlledCamera.orthographicSize * zoom, radius * 0.01f, radius * 1000f);
        ApplyView();
    }

    // Place the camera 'distance' units behind the pivot along its forward axis and
    // update the near/far clipping planes so the whole molecule stays visible.
    private void ApplyView()
    {
        transform.position = pivot - transform.forward * distance;
        // Keep the complete molecule between the clipping planes when fitted.
        float extent = radius + Vector3.Distance(pivot, molecule != null ? molecule.transform.position : pivot);
        controlledCamera.nearClipPlane = Mathf.Max(0.001f, Mathf.Min(radius * 0.01f, distance * 0.01f));
        controlledCamera.farClipPlane = Mathf.Max(controlledCamera.nearClipPlane + 1f,
            distance + extent * 2f);
    }
}
