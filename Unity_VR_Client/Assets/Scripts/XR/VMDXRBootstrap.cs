using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Management;

// Chooses how the viewer runs and sets the scene up for that mode. It runs once at startup, before
// every other script (execution order -1000), so VMDCameraController finds the right Camera.main.
//
// Modes:
// - Desktop: "-desktop" on a player's command line, no XR settings, or XR failed to start.
// - Simulator: in the Editor while simulateInEditor is set (XR Interaction Simulator).
// - Headset: otherwise; an OpenXR runtime and headset then drive the XR Origin.
//
// Startup:
// - Awake chooses the mode, activates the matching camera rig and, in Simulator and Headset modes,
//   adds the XR pieces: VMDMoleculeGrab on the molecule, VMDVRController, VMDRayHighlight,
//   VMDStatusPanel, the XR UI input module and the plain background. Headset mode also adds
//   VMDIndexGrip.
// - XR itself starts in Start, a few frames later. XR Plug-in Management does not initialize XR on
//   startup, so a missing runtime never blocks Play mode. Starting XR in Awake, before the player
//   had applied its startup window resolution, made that resolution switch fail or hang on a PC
//   with both integrated and NVIDIA graphics.
// - If XR does not start, the scene reloads once in Desktop mode.
//
// Placement: when the molecule first appears it is turned toward the user, scaled and placed in
// front of the head (LateUpdate, FitMolecule). Recenter and ToggleImmersive place it again.
//
// Placement defaults were tuned on a Valve Index and apply to every headset. See VR_ISSUES.md
// for the tested hardware and headset launch procedures.
[DefaultExecutionOrder(-1000)]
public class VMDXRBootstrap : MonoBehaviour
{
    public enum Mode { Desktop, Simulator, Headset }

    // Scenes without a bootstrap stay in Desktop mode, exactly as before.
    public static Mode ActiveMode { get; private set; } = Mode.Desktop;

    // Set when XR failed to start, so the reloaded scene stays in Desktop mode.
    private static bool forceDesktop;

    // The project enters Play mode without a domain reload, so static fields keep their values
    // from the previous Play session. Reset them, or one failed XR start would keep every later
    // session in Desktop mode until scripts are recompiled.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        forceDesktop = false;
        ActiveMode = Mode.Desktop;
    }
    // XR starts this many frames, plus this many seconds, after Awake (see the header).
    private const int StartupFramesBeforeXR = 3;
    private const float StartupSecondsBeforeXR = 0.5f;

    // The two camera rigs of the scene; only the one for the chosen mode is active. The simulator
    // object is active in Simulator mode only.
    [SerializeField] private GameObject desktopCamera;
    [SerializeField] private GameObject xrOrigin;
    [SerializeField] private GameObject interactionSimulator;
    [Tooltip("In the Editor, use the XR Interaction Simulator instead of a headset.")]
    [SerializeField] private bool simulateInEditor = true;
    // Found in the scene when not assigned.
    [SerializeField] private MoleculeRenderer molecule;
    // Where the fitted molecule's center goes: x to the right, y above the floor, z ahead, measured
    // from the floor point below the head along the head's heading (ViewTarget), not from the
    // XR Origin's own axes.
    [Tooltip("Molecule center relative to the XR Origin, in meters.")]
    [SerializeField] private Vector3 moleculeOffset = new Vector3(0f, 1.3f, 1.5f);
    [Tooltip("Radius of the molecule's bounding sphere after fitting, in meters.")]
    // 0.4 m looked like a desk model in the Valve Index; 0.8 m is about person-sized.
    [SerializeField, Min(0.01f)] private float moleculeRadius = 0.8f;
    [Tooltip("Radius in immersive size (right thumbstick click), in meters; the user stands inside the molecule.")]
    [SerializeField, Min(0.01f)] private float immersiveRadius = 4f;
    [Tooltip("In immersive size, how far ahead of the head the molecule's center sits, as a fraction of its radius.")]
    [SerializeField, Range(0f, 1f)] private float immersiveCenterAhead = 0.3f;
    [Tooltip("Background in Simulator and Headset modes. The default skybox's horizon and ground do not match the real floor and distract from the molecule; the desktop camera keeps the skybox.")]
    [SerializeField] private Color xrBackground = new Color(0.16f, 0.17f, 0.19f);

    // Reusable buffer for measuring the molecule's bounds.
    private readonly List<Renderer> renderers = new List<Renderer>();
    private VMDStatusPanel statusPanel;
    // startedXR: this component started the XR loader, so it also shuts it down.
    // oriented, fitted: the first placement has turned, then scaled and positioned, the molecule.
    // immersive: the molecule is at immersive size (ToggleImmersive).
    private bool startedXR, fitted, oriented, immersive;
    // Frame at which to fit again, -1 when no fit is pending.
    private int refitFrame = -1;
    // The first fit waits for a head pose (at most this long), so it lands where the user is
    // looking; headWaitUntil is the deadline.
    private const float HeadPoseWaitSeconds = 5f;
    private float headWaitUntil;

    // Choose the mode, enable the camera rig for it (the other rig stays inactive), and in
    // Simulator and Headset modes add the XR components listed in the header.
    private void Awake()
    {
        ActiveMode = ChooseMode();
        bool xr = ActiveMode != Mode.Desktop;
        // Before the rig is active, so its interactors register with the XR UI module.
        if (xr) UseXRUIInput();
        if (desktopCamera != null) desktopCamera.SetActive(!xr);
        if (xrOrigin != null) xrOrigin.SetActive(xr);
        // Plain background instead of the skybox; see the xrBackground tooltip.
        if (xr && xrOrigin != null)
            foreach (Camera headCamera in xrOrigin.GetComponentsInChildren<Camera>(true))
            {
                headCamera.clearFlags = CameraClearFlags.SolidColor;
                headCamera.backgroundColor = xrBackground;
            }
        if (interactionSimulator != null) interactionSimulator.SetActive(ActiveMode == Mode.Simulator);
        if (molecule == null) molecule = FindAnyObjectByType<MoleculeRenderer>();
        // After the rig is active, so the grab interactable joins the interactors' interaction
        // manager.
        if (xr && molecule != null && molecule.GetComponent<VMDMoleculeGrab>() == null)
            molecule.gameObject.AddComponent<VMDMoleculeGrab>();
        if (xr && GetComponent<VMDVRController>() == null)
            gameObject.AddComponent<VMDVRController>();
        if (xr && GetComponent<VMDRayHighlight>() == null)
            gameObject.AddComponent<VMDRayHighlight>();
        // Only real controllers can be Index controllers; the simulator needs no squeeze grab.
        if (ActiveMode == Mode.Headset && GetComponent<VMDIndexGrip>() == null)
            gameObject.AddComponent<VMDIndexGrip>();
        if (xr)
            statusPanel = VMDStatusPanel.Create();
        // The desktop orbit camera. Needed here for the scene reloaded after a failed XR start,
        // where the camera controller's own AfterSceneLoad hook does not run again.
        if (!xr)
            VMDCameraController.AttachToMainCamera();
        Debug.Log($"[VMDXRBootstrap] {ActiveMode} mode.", this);
    }

    // Headset mode: start XR once the startup frames have run. Without a runtime or headset,
    // reload the scene, which then comes up in Desktop mode (forceDesktop).
    private IEnumerator Start()
    {
        if (ActiveMode != Mode.Headset || startedXR)
            yield break;
        for (int i = 0; i < StartupFramesBeforeXR; i++)
            yield return null;
        yield return new WaitForSecondsRealtime(StartupSecondsBeforeXR);
        if (TryStartXR())
        {
            headWaitUntil = Time.unscaledTime + HeadPoseWaitSeconds;
            Debug.Log("[VMDXRBootstrap] XR started.", this);
            yield break;
        }
        forceDesktop = true;
        SceneManager.LoadScene(gameObject.scene.buildIndex);
    }

    // Controller rays press world-space buttons through XRUIInputModule, not the desktop
    // InputSystemUIInputModule; desktop mode keeps the scene's module untouched.
    private static void UseXRUIInput()
    {
        var events = FindAnyObjectByType<EventSystem>();
        if (events == null) events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        var desktopInput = events.GetComponent<InputSystemUIInputModule>();
        if (desktopInput != null) desktopInput.enabled = false;
        if (events.GetComponent<XRUIInputModule>() == null) events.gameObject.AddComponent<XRUIInputModule>();
    }

    // Desktop when forced (by "-desktop" or a failed XR start), Simulator in the Editor when
    // simulateInEditor is set, Headset when XR settings exist, Desktop otherwise.
    private Mode ChooseMode()
    {
        if (forceDesktop || Array.Exists(Environment.GetCommandLineArgs(),
                argument => string.Equals(argument, "-desktop", StringComparison.OrdinalIgnoreCase)))
            return Mode.Desktop;
        if (Application.isEditor && simulateInEditor)
            return Mode.Simulator;
        // XR itself starts in Start; Desktop follows by a scene reload if it fails.
        return XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null
            ? Mode.Headset
            : Mode.Desktop;
    }

    // Initialize the OpenXR loader synchronously and start its subsystems. Returns false when no
    // loader becomes active, which is the case without a runtime or headset.
    private bool TryStartXR()
    {
        XRManagerSettings manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager == null)
            return false;
        if (manager.activeLoader == null)
            manager.InitializeLoaderSync();
        if (manager.activeLoader == null)
        {
            Debug.Log("[VMDXRBootstrap] No OpenXR runtime or headset found; reloading in desktop mode.", this);
            return false;
        }
        manager.StartSubsystems();
        startedXR = true;
        return true;
    }

    // Fit the molecule in front of the user when it first appears. Later rebuilds of the geometry
    // (drawing-method switch, reconnect) keep the user's grabs and scaling; Recenter fits again.
    private void LateUpdate()
    {
        if (ActiveMode == Mode.Desktop || molecule == null || xrOrigin == null)
            return;
        // Headset: wait until XR has started and the head is tracked, or the wait has run out.
        if (!oriented && ActiveMode == Mode.Headset && (!startedXR || (!HeadTracked() && Time.unscaledTime < headWaitUntil)))
            return;
        // Nothing is drawn yet.
        if (!VMDCameraController.TryGetMoleculeBounds(molecule, renderers, out Bounds bounds))
            return;
        if (!oriented)
        {
            // Turn the molecule's initial front toward the user; fit next frame with the rotated
            // bounds.
            oriented = true;
            molecule.transform.rotation = ViewRotation();
            refitFrame = Time.frameCount + 1;
            return;
        }
        if (!fitted || (refitFrame >= 0 && Time.frameCount >= refitFrame))
        {
            refitFrame = -1;
            FitMolecule(bounds);
        }
    }

    // Undo the user's grabs: restore the initial orientation facing the user, then fit size and
    // position again in front of where the user is looking, and put the status panel back in
    // view. Called by VMDVRController (left thumbstick click) and by ToggleImmersive.
    public void Recenter()
    {
        if (ActiveMode == Mode.Desktop || molecule == null || xrOrigin == null)
            return;
        molecule.transform.rotation = ViewRotation();
        // The bounds are recomputed in this frame's LateUpdate, after this one, so fit on the
        // next frame.
        refitFrame = Time.frameCount + 1;
        if (statusPanel != null)
            statusPanel.PlaceInView();
    }

    // Switch between the in-front size (moleculeRadius) and immersive size (immersiveRadius, around
    // the user), placed from where the user is looking now. Called by VMDVRController (right
    // thumbstick click).
    public void ToggleImmersive()
    {
        if (ActiveMode == Mode.Desktop || molecule == null || xrOrigin == null)
            return;
        immersive = !immersive;
        Debug.Log($"[VMDXRBootstrap] {(immersive ? "Immersive" : "In-front")} size.", this);
        Recenter();
    }

    // Whether the head camera has a pose yet: an untracked XR camera sits at its Camera Offset's
    // origin.
    private static bool HeadTracked()
    {
        Camera head = Camera.main;
        return head != null && head.transform.localPosition.sqrMagnitude > 1e-6f;
    }

    // The head's horizontal heading around the XR Origin's up axis; the origin's forward without a
    // head, or when the head looks straight up or down.
    // The play-area forward need not match the direction the user faces: seen with SteamVR and an
    // Index, where the molecule appeared to the user's left. A Quest's Link space can differ too.
    private Quaternion ViewRotation()
    {
        Transform origin = xrOrigin.transform;
        Camera head = Camera.main;
        Vector3 forward = head != null ? Vector3.ProjectOnPlane(head.transform.forward, origin.up) : Vector3.zero;
        if (forward.sqrMagnitude < 1e-4f)
            forward = origin.forward;
        return Quaternion.LookRotation(forward, origin.up);
    }

    // Immersive center: at head height, slightly ahead, so the user stands inside the molecule.
    private Vector3 ImmersiveTarget(float targetRadius)
    {
        Camera head = Camera.main;
        Vector3 eye = head != null ? head.transform.position : xrOrigin.transform.position;
        return eye + ViewRotation() * (Vector3.forward * (targetRadius * immersiveCenterAhead));
    }

    // moleculeOffset measured from the floor point below the head, along the head's heading.
    private Vector3 ViewTarget()
    {
        Transform origin = xrOrigin.transform;
        Camera head = Camera.main;
        Vector3 floor = origin.position;
        if (head != null)
        {
            Vector3 headPosition = head.transform.position;
            floor = headPosition - origin.up * Vector3.Dot(headPosition - origin.position, origin.up);
        }
        return floor + ViewRotation() * Vector3.Scale(moleculeOffset, origin.lossyScale);
    }

    // Scale and place the molecule now, at the in-front or the immersive size, whichever is
    // selected. Does nothing while no geometry is drawn.
    public void FitMolecule()
    {
        if (molecule != null && VMDCameraController.TryGetMoleculeBounds(molecule, renderers, out Bounds bounds))
            FitMolecule(bounds);
    }

    // Scale the molecule so its bounding sphere has the target radius, and move it so the bounds
    // center lands on the target point. VMD's view matrix sets the rendered size, so fit by
    // measured bounds rather than by Angstroms.
    private void FitMolecule(Bounds bounds)
    {
        float radius = bounds.extents.magnitude;
        if (radius <= 0f || xrOrigin == null)
            return;
        Transform root = molecule.transform;
        float targetRadius = immersive ? immersiveRadius : moleculeRadius;
        float factor = targetRadius / radius;
        Vector3 target = immersive ? ImmersiveTarget(targetRadius) : ViewTarget();
        // Scaling about the root moves the bounds center to root + (center - root) * factor.
        root.position = target - (bounds.center - root.position) * factor;
        root.localScale *= factor;
        fitted = true;
        // Bounds before fitting, for diagnosing an unexpected size or shape on the headset.
        Debug.Log($"[VMDXRBootstrap] Fitted molecule: bounds size {bounds.size}, radius {radius:F3} -> {targetRadius} m, " +
                  $"scale x{factor:G4}, lossyScale {root.lossyScale}, center {target}.", this);
    }

    // Shut down XR only if this bootstrap started it.
    private void OnDestroy()
    {
        ActiveMode = Mode.Desktop;
        if (!startedXR)
            return;
        XRManagerSettings manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager != null && manager.activeLoader != null)
        {
            manager.StopSubsystems();
            manager.DeinitializeLoader();
        }
        startedXR = false;
    }
}
