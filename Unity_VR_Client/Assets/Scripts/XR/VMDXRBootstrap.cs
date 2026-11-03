using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;

// Chooses how the viewer runs, once at startup and before VMDCameraController attaches to Camera.main:
// Headset when an OpenXR runtime starts, Simulator in the Editor (XR Interaction Simulator),
// Desktop otherwise. XR Plug-in Management does not initialize XR on startup, so a missing runtime
// never blocks Play mode. "-desktop" on a player's command line forces Desktop.
[DefaultExecutionOrder(-1000)]
public class VMDXRBootstrap : MonoBehaviour
{
    public enum Mode { Desktop, Simulator, Headset }

    // Scenes without a bootstrap stay in Desktop mode, exactly as before.
    public static Mode ActiveMode { get; private set; } = Mode.Desktop;

    [SerializeField] private GameObject desktopCamera;
    [SerializeField] private GameObject xrOrigin;
    [SerializeField] private GameObject interactionSimulator;
    [Tooltip("In the Editor, use the XR Interaction Simulator instead of a headset.")]
    [SerializeField] private bool simulateInEditor = true;
    [SerializeField] private MoleculeRenderer molecule;
    [Tooltip("Molecule center relative to the XR Origin, in meters.")]
    [SerializeField] private Vector3 moleculeOffset = new Vector3(0f, 1.3f, 0.8f);
    [Tooltip("Radius of the molecule's bounding sphere after fitting, in meters.")]
    [SerializeField, Min(0.01f)] private float moleculeRadius = 0.4f;

    private readonly List<Renderer> renderers = new List<Renderer>();
    private bool startedXR, fitted;

    // Enable the camera rig for the chosen mode; the other rig stays inactive.
    private void Awake()
    {
        ActiveMode = ChooseMode();
        bool xr = ActiveMode != Mode.Desktop;
        if (desktopCamera != null) desktopCamera.SetActive(!xr);
        if (xrOrigin != null) xrOrigin.SetActive(xr);
        if (interactionSimulator != null) interactionSimulator.SetActive(ActiveMode == Mode.Simulator);
        if (molecule == null) molecule = FindAnyObjectByType<MoleculeRenderer>();
        Debug.Log($"[VMDXRBootstrap] {ActiveMode} mode.", this);
    }

    private Mode ChooseMode()
    {
        if (Array.Exists(Environment.GetCommandLineArgs(),
                argument => string.Equals(argument, "-desktop", StringComparison.OrdinalIgnoreCase)))
            return Mode.Desktop;
        if (Application.isEditor && simulateInEditor)
            return Mode.Simulator;
        return TryStartXR() ? Mode.Headset : Mode.Desktop;
    }

    // Initialize the OpenXR loader synchronously; without a runtime or headset no loader becomes active.
    private bool TryStartXR()
    {
        XRManagerSettings manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (manager == null)
            return false;
        if (manager.activeLoader == null)
            manager.InitializeLoaderSync();
        if (manager.activeLoader == null)
        {
            Debug.Log("[VMDXRBootstrap] No OpenXR runtime or headset found; using desktop mode.", this);
            return false;
        }
        manager.StartSubsystems();
        startedXR = true;
        return true;
    }

    // Fit the molecule in front of the user whenever it (re)appears, e.g. after a reconnect.
    private void LateUpdate()
    {
        if (ActiveMode == Mode.Desktop || molecule == null || xrOrigin == null)
            return;
        if (!VMDCameraController.TryGetMoleculeBounds(molecule, renderers, out Bounds bounds))
        {
            fitted = false;
            return;
        }
        if (!fitted)
            FitMolecule(bounds);
    }

    // Scale the molecule to moleculeRadius and center it at moleculeOffset from the XR Origin.
    public void FitMolecule()
    {
        if (molecule != null && VMDCameraController.TryGetMoleculeBounds(molecule, renderers, out Bounds bounds))
            FitMolecule(bounds);
    }

    // VMD's view matrix sets the rendered size, so fit by measured bounds rather than by Angstroms.
    private void FitMolecule(Bounds bounds)
    {
        float radius = bounds.extents.magnitude;
        if (radius <= 0f || xrOrigin == null)
            return;
        Transform root = molecule.transform;
        float factor = moleculeRadius / radius;
        Vector3 target = xrOrigin.transform.TransformPoint(moleculeOffset);
        // Scaling about the root moves the bounds center to root + (center - root) * factor.
        root.position = target - (bounds.center - root.position) * factor;
        root.localScale *= factor;
        fitted = true;
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
