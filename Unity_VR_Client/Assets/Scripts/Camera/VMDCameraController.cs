using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Left drag: orbit. Right/middle drag: pan. Wheel: zoom. F: fit molecule.
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public class VMDCameraController : MonoBehaviour
{
    [SerializeField] private MoleculeRenderer molecule;
    [SerializeField, Min(1f)] private float fitPadding = 1.15f;
    [SerializeField, Min(0.01f)] private float rotationSpeed = 0.25f;
    [SerializeField, Min(0.0001f)] private float zoomSpeed = 0.012f;

    private readonly List<Renderer> renderers = new List<Renderer>();
    private Camera controlledCamera;
    private Vector3 pivot;
    private float distance;
    private float radius;
    private bool fitted;

    // Attach only at runtime, so existing scenes and camera settings stay untouched.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AttachToMainCamera()
    {
        Camera main = Camera.main;
        if (main != null && FindAnyObjectByType<MoleculeRenderer>() != null &&
            main.GetComponent<VMDCameraController>() == null)
            main.gameObject.AddComponent<VMDCameraController>();
    }

    private void Awake()
    {
        controlledCamera = GetComponent<Camera>();
    }

    private bool TryGetBounds(out Bounds bounds)
    {
        bounds = default;
        if (molecule == null)
            molecule = FindAnyObjectByType<MoleculeRenderer>();
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
        return found;
    }

    public void FitMolecule()
    {
        if (!TryGetBounds(out Bounds bounds))
            return;

        pivot = bounds.center;
        radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
        float aspect = Mathf.Max(controlledCamera.aspect, 0.01f);
        float verticalHalfAngle = controlledCamera.fieldOfView * Mathf.Deg2Rad * 0.5f;
        float horizontalHalfAngle = Mathf.Atan(Mathf.Tan(verticalHalfAngle) * aspect);
        // The bounding sphere fits at every orbit angle, including portrait windows.
        distance = radius * fitPadding /
            Mathf.Sin(Mathf.Min(verticalHalfAngle, horizontalHalfAngle));
        if (controlledCamera.orthographic)
            controlledCamera.orthographicSize = radius * fitPadding / Mathf.Min(1f, aspect);
        fitted = true;
        ApplyView();
    }

    private void LateUpdate()
    {
        // Coordinates arrive asynchronously; fit when the first atoms become visible.
        if (!fitted)
        {
            FitMolecule();
            if (!fitted)
                return;
        }

        if (!Application.isFocused)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
        {
            FitMolecule();
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse == null || !controlledCamera.pixelRect.Contains(mouse.position.ReadValue()))
            return;

        Vector2 delta = mouse.delta.ReadValue();
        if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
        {
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

        float zoom = Mathf.Exp(Mathf.Clamp(-mouse.scroll.ReadValue().y * zoomSpeed, -2f, 2f));
        distance = Mathf.Clamp(distance * zoom, radius * 0.02f, radius * 1000f);
        if (controlledCamera.orthographic)
            controlledCamera.orthographicSize = Mathf.Clamp(
                controlledCamera.orthographicSize * zoom, radius * 0.01f, radius * 1000f);
        ApplyView();
    }

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
