using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

// Highlights the structure the right controller's ray points at, in Simulator and Headset modes.
// VMDXRBootstrap adds this component there; the desktop keeps the H key (Cycle).
//
// Each frame:
// 1. VMDHighlightController.PrepareStructures supplies the atoms of every entry of its Selections
//    list (by default N_DOMAIN, C_DOMAIN, LINKER), resolved once per connection.
// 2. The first of those atoms along the ray is found (Pick): for QuickSurf where the ray enters
//    the drawn surface, for VDW and CPK where it enters the drawn sphere, for NewCartoon within
//    pickRadius angstroms of the ray. Surfaces and spheres that contain the hand are passed
//    through.
// While the toolkit hides its own ray (hand inside the molecule), a pointer line shows the aim.
// 3. The entry that contains that atom is highlighted (ShowStructure); pointing at another entry
//    switches to it. When the ray leaves the molecule, the highlight is cleared after
//    releaseDelay, so it does not flicker in the gaps between atoms.
//
// The ray is the right hand's Near-Far interactor of the XR Origin, the one that grabs the
// molecule and presses panel buttons. While it rests on the status panel nothing is picked.
// Like every highlight, it works only on the live VDW, CPK, NewCartoon and QuickSurf route.
// The status panel's Highlight button turns pointing on and off (TogglePointing). A highlight
// chosen with the H key in Headset mode stays until the ray points at a structure.
public class VMDRayHighlight : MonoBehaviour
{
    [Tooltip("A NewCartoon atom counts as pointed at within this distance of the ray, in angstroms. VDW, CPK and QuickSurf use what is drawn.")]
    [SerializeField, Min(0.1f)] private float pickRadius = 4f;
    [Tooltip("Seconds the highlight stays after the ray leaves the molecule.")]
    [SerializeField, Min(0f)] private float releaseDelay = 0.25f;
    // Seconds between searches for the ray interactor while none is found.
    private const float SearchInterval = 1f;

    // Pointing selects the highlight; off leaves the highlight to the H key.
    public bool Pointing { get; private set; } = true;

    private VMDHighlightController highlight;
    private MoleculeRenderer molecule;
    // The right hand's ray, as an interactor (enabled state) and as its curve data (origin, end).
    private XRBaseInputInteractor rayInteractor;
    private ICurveInteractionDataProvider ray;
    private float nextSearch;
    // Time the ray left the highlighted structure, or negative while it is on one.
    private float lostSince = -1f;

    // Switch pointing on or off; switching off removes a highlight that pointing chose.
    // Called by the status panel's Highlight button.
    public void TogglePointing()
    {
        Pointing = !Pointing;
        lostSince = -1f;
        if (!Pointing && highlight != null && highlight.ActiveStructure >= 0)
            highlight.Clear();
    }

    private void Update()
    {
        if (!Pointing)
        {
            ShowPointer(false, default);
            return;
        }
        bool hasRay = TryGetRay(out Ray worldRay);
        hitDistance = float.PositiveInfinity;
        int target = Target(hasRay, worldRay);
        ShowPointer(hasRay, worldRay);
        if (highlight == null)
            return;

        if (target >= 0)
        {
            lostSince = -1f;
            if (target != highlight.ActiveStructure)
                highlight.ShowStructure(target);
            return;
        }
        // Only a highlight chosen by pointing is cleared; one from the H key stays.
        if (highlight.ActiveStructure < 0)
        {
            lostSince = -1f;
            return;
        }
        if (lostSince < 0f)
            lostSince = Time.unscaledTime;
        else if (Time.unscaledTime - lostSince >= releaseDelay)
        {
            lostSince = -1f;
            highlight.Clear();
        }
    }

    // The structure the ray points at, -1 for none. Two kinds of hit, the nearer one wins:
    // - QuickSurf atoms: where the ray enters the drawn surface (VMDQuickSurfScene.TryRaycast).
    //   The surface reaches several angstroms beyond the atom centers, so a distance test missed
    //   its edges.
    // - Other atoms: the drawn VDW/CPK sphere, or pickRadius around a NewCartoon atom
    //   (VMDCoordinateScene.TryPickAtom).
    // Surfaces and spheres that contain the hand are passed through, so at immersive size the
    // ray picks what it points at. When it meets nothing else, the QuickSurf structure that
    // contains the hand is picked.
    private int Pick(VMDCoordinateScene scene, int[] atoms, Ray worldRay)
    {
        VMDQuickSurfScene surface = scene.GetComponent<VMDQuickSurfScene>();
        bool[] drawnAsSurface = surface != null ? surface.SurfaceAtoms : null;
        int best = -1, inside = -1;
        float bestDistance = float.PositiveInfinity;
        if (drawnAsSurface != null)
        {
            if (surface.TryRaycast(worldRay, out float surfaceDistance, out int surfaceAtom, out int insideAtom) &&
                highlight.StructureOf(surfaceAtom) >= 0)
            {
                best = surfaceAtom;
                bestDistance = surfaceDistance;
            }
            inside = insideAtom;
        }
        int[] others = OtherAtoms(atoms, drawnAsSurface);
        if (others.Length > 0 && scene.TryPickAtom(worldRay, others, pickRadius, out int atom, out float distance) &&
            distance < bestDistance)
        {
            best = atom;
            bestDistance = distance;
        }
        if (best < 0)
            best = inside;
        else
            hitDistance = bestDistance;
        return best >= 0 ? highlight.StructureOf(best) : -1;
    }

    // The structure the ray points at this frame, -1 for none or while the molecule, the
    // highlight controller, the frame on display or the resolved structures are missing.
    private int Target(bool hasRay, Ray worldRay)
    {
        if (molecule == null) molecule = FindAnyObjectByType<MoleculeRenderer>();
        if (highlight == null) highlight = FindAnyObjectByType<VMDHighlightController>();
        if (highlight == null || molecule == null)
            return -1;
        VMDCoordinateScene scene = molecule.GetComponentInChildren<VMDCoordinateScene>();
        if (scene == null || !scene.HasFrame)
            return -1;
        // Called even without a ray, so the structures are resolved as soon as VMD connects.
        int[] atoms = highlight.PrepareStructures(scene.AtomCount);
        return atoms != null && hasRay ? Pick(scene, atoms, worldRay) : -1;
    }

    // World distance along the ray to what the last Pick hit; infinite when it hit nothing, or
    // only fell back to the surface that contains the hand. Ends the pointer line.
    private float hitDistance = float.PositiveInfinity;

    // XR Interaction Toolkit hides the Near-Far interactor's ray while the hand is inside the
    // molecule's grab volume, which at immersive size is all the time, so the user cannot see
    // where the hand aims. This line stands in for it then: from the hand to what the pick hit,
    // or pointerLength meters ahead. Hidden while the toolkit's own ray shows.
    private void ShowPointer(bool hasRay, Ray worldRay)
    {
        bool visible = hasRay && !ray.isActive;
        if (!visible || (pointer == null && !CreatePointer()))
        {
            if (pointer != null) pointer.enabled = false;
            return;
        }
        pointer.enabled = true;
        pointer.SetPosition(0, pointerOrigin);
        pointer.SetPosition(1, worldRay.GetPoint(float.IsInfinity(hitDistance) ? pointerLength : hitDistance));
    }

    // Create the pointer line with the material of the interactor's own line, so it looks
    // alike and its shader is included in player builds; URP's unlit shader otherwise.
    private bool CreatePointer()
    {
        LineRenderer template = rayInteractor != null ? rayInteractor.GetComponentInChildren<LineRenderer>(true) : null;
        Material material = template != null ? template.sharedMaterial : null;
        if (material == null)
        {
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null)
                return false;
            material = new Material(unlit);
        }
        pointer = new GameObject("VMD Pointer").AddComponent<LineRenderer>();
        pointer.useWorldSpace = true;
        pointer.positionCount = 2;
        pointer.sharedMaterial = material;
        pointer.widthMultiplier = 0.004f;
        pointer.numCapVertices = 2;
        pointer.startColor = new Color(1f, 1f, 1f, 0.9f);
        pointer.endColor = new Color(1f, 1f, 1f, 0.5f);
        pointer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        pointer.receiveShadows = false;
        return true;
    }

    private void OnDisable()
    {
        if (pointer != null) pointer.enabled = false;
    }

    private void OnDestroy()
    {
        if (pointer != null) Destroy(pointer.gameObject);
    }

    [Tooltip("Length of the pointer line, in meters, when the ray hits nothing.")]
    [SerializeField, Min(0.1f)] private float pointerLength = 3f;
    private LineRenderer pointer;

    // 'atoms' without those drawn as a QuickSurf surface; cached while both inputs stay the same.
    private int[] OtherAtoms(int[] atoms, bool[] drawnAsSurface)
    {
        if (drawnAsSurface == null)
            return atoms;
        if (atoms != otherSource || drawnAsSurface != otherMask)
        {
            var list = new System.Collections.Generic.List<int>(atoms.Length);
            foreach (int atom in atoms)
                if (atom >= drawnAsSurface.Length || !drawnAsSurface[atom]) list.Add(atom);
            otherAtoms = list.ToArray();
            otherSource = atoms;
            otherMask = drawnAsSurface;
        }
        return otherAtoms;
    }
    private int[] otherAtoms, otherSource;
    private bool[] otherMask;

    // The right hand's ray in world space. False while there is none, it is disabled, or it
    // rests on UI (the status panel). The curve's isActive is not required: the Near-Far
    // interactor turns its far ray off while the hand is inside the molecule's grab volume,
    // which is always the case at immersive size, yet the controller still aims.
    private bool TryGetRay(out Ray worldRay)
    {
        worldRay = default;
        if ((rayInteractor == null || !rayInteractor.isActiveAndEnabled) && Time.unscaledTime >= nextSearch)
            FindRay();
        if (rayInteractor == null || !rayInteractor.isActiveAndEnabled || ray.curveOrigin == null)
            return false;
        if (ray.TryGetCurveEndPoint(out _) == EndPointType.UI)
            return false;
        pointerOrigin = ray.curveOrigin.position;
        worldRay = new Ray(pointerOrigin, ray.curveOrigin.forward);
        // Simulator: the mouse cursor is what the user aims with. The simulator turns the
        // controller toward the point where the cursor's physics ray hits, which is the surface
        // of the molecule's large grab sphere, so the controller's own ray passes beside the atom
        // under the cursor. Pick along the cursor's ray instead; the pointer line still starts at
        // the controller.
        Camera head = Camera.main;
        Mouse mouse = Mouse.current;
        if (VMDXRBootstrap.ActiveMode == VMDXRBootstrap.Mode.Simulator && head != null && mouse != null)
            worldRay = head.ScreenPointToRay(mouse.position.ReadValue());
        return true;
    }

    // Start of the pointer line: the controller, also when the pick ray is the mouse cursor's.
    private Vector3 pointerOrigin;

    // Find the right hand's ray: its Near-Far interactor, or else another active interactor
    // with a curve (not the teleport ray, which is inactive unless teleporting).
    private void FindRay()
    {
        nextSearch = Time.unscaledTime + SearchInterval;
        rayInteractor = null;
        ray = null;
        foreach (var interactor in FindObjectsByType<XRBaseInputInteractor>(FindObjectsSortMode.None))
        {
            if (interactor.handedness != InteractorHandedness.Right || !interactor.isActiveAndEnabled ||
                !(interactor is ICurveInteractionDataProvider curve))
                continue;
            if (interactor is NearFarInteractor)
            {
                rayInteractor = interactor;
                ray = curve;
                return;
            }
            if (ray == null && curve.isActive)
            {
                rayInteractor = interactor;
                ray = curve;
            }
        }
    }
}
