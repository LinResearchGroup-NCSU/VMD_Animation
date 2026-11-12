using UnityEngine;
using System.Collections.Generic;

// The molecule's root object, and its simplest view: one atomPrefab instance per atom, placed
// from VMD coordinates in this object's local coordinate space (the "atom view").
//
// Its roles:
// - Root: the display routes put their geometry under this transform ("VMD coordinate scene" from
//   TrajectoryController, "VMD static scene" from RepresentationController). VMDCameraController
//   and the XR components measure and move the molecule through this object.
// - Startup: OnEnable adds and initializes the RepresentationController, which in turn sets up
//   the TrajectoryController. Update sends FRAME 0 once per connection; VMDClient answers the
//   reply with a coordinate request, and that first frame starts both controllers.
// - Atom view: RenderMolecule receives every coordinate frame from VMDClient. It first asks the
//   RepresentationController whether a VMD representation is drawing the molecule; only when it
//   is not (the atom view was requested, or the mesh route is not in use) are the atoms drawn.
public class MoleculeRenderer : MonoBehaviour
{
    public GameObject atomPrefab; // Prefab for rendering atoms
    // Source of coordinates. When empty, a VMDClient on this object or in the scene is used.
    [SerializeField] private VMDClient client;
    // Unity units per VMD coordinate unit. Applies to the atom view, the coordinate-route scenes
    // and the imported static scene (through CoordinateScale).
    [SerializeField, Min(0.0001f)] private float coordinateScale = 1f;
    // Diameter in VMD coordinate units; scaled together with atom positions.
    [SerializeField, Min(0.0001f)] private float atomSize = 1.5f;

    // Atom instances of the atom view; the list index is the atom index. Instances are reused
    // between frames and deactivated, not destroyed, when no longer needed.
    private readonly List<GameObject> atoms = new List<GameObject>();
    // The client whose CoordinatesReceived event this renderer is subscribed to; null while the
    // component is disabled or no client was found.
    private VMDClient subscribedClient;
    // FRAME 0 has been sent on the current connection.
    private bool requestedInitialCoordinates;
    private RepresentationController representationController;
    public float CoordinateScale => coordinateScale;

    // Resolve the VMDClient, attach and initialize the RepresentationController,
    // subscribe to coordinate updates, and render any coordinates already cached.
    private void OnEnable()
    {
        if (client == null)
            client = GetComponent<VMDClient>();
        if (client == null)
            client = FindAnyObjectByType<VMDClient>();

        if (client == null)
        {
            Debug.LogWarning("[MoleculeRenderer] Assign a VMDClient to receive coordinates.", this);
            return;
        }

        subscribedClient = client;
        representationController = GetComponent<RepresentationController>();
        if (representationController == null)
            representationController = gameObject.AddComponent<RepresentationController>();
        representationController.Initialize(client, this);
        subscribedClient.CoordinatesReceived += RenderMolecule;
        requestedInitialCoordinates = false;
        RenderMolecule(subscribedClient.Coordinates);
    }

    // Request the first coordinate frame once the asynchronous connection is up;
    // reset the request flag on disconnect so it is requested again after reconnecting.
    // The flag stays false, and the command is sent again next frame, until SendCommand succeeds.
    private void Update()
    {
        if (subscribedClient == null)
            return;

        if (!subscribedClient.IsConnected)
        {
            requestedInitialCoordinates = false;
            return;
        }

        // Start each connection at VMD frame 0, not the previously selected frame.
        // VMDClient requests coordinates after receiving the OK FRAME acknowledgement.
        if (!requestedInitialCoordinates)
        {
            requestedInitialCoordinates = subscribedClient.SendCommand("FRAME 0");
        }
    }

    // Handle one frame of atom coordinates. Called by VMDClient.CoordinatesReceived, by OnEnable
    // with the coordinates already cached, and by RepresentationController.ShowAtomView.
    // The RepresentationController is told about the frame first. When it reports that a VMD
    // representation draws the molecule, the atoms are hidden and nothing else happens here.
    // Otherwise the atom view is drawn: validate every position, center the frame on its
    // mean position, then create or reuse atom instances and hide surplus ones.
    public void RenderMolecule(IReadOnlyList<Vector3> coordinates)
    {
        if (coordinates == null)
        {
            Debug.LogWarning("[MoleculeRenderer] Coordinates cannot be null.", this);
            return;
        }

        // An empty frame (no coordinates received yet) skips this and hides every atom below.
        if (coordinates.Count > 0 && representationController != null &&
            representationController.OnCoordinatesUpdated())
        {
            HideLegacyAtoms();
            return;
        }

        if (coordinates.Count > 0 && atomPrefab == null)
        {
            Debug.LogWarning("[MoleculeRenderer] Assign an atom prefab before rendering.", this);
            return;
        }

        // Validate the whole frame before changing the visible molecule.
        double centerX = 0, centerY = 0, centerZ = 0;
        for (int i = 0; i < coordinates.Count; i++)
        {
            Vector3 position = coordinates[i] * coordinateScale;
            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
            {
                Debug.LogWarning($"[MoleculeRenderer] Invalid coordinates for atom {i}.", this);
                return;
            }

            centerX += coordinates[i].x;
            centerY += coordinates[i].y;
            centerZ += coordinates[i].z;
        }

        // Center each frame on the average atom position before applying scale.
        Vector3 center = coordinates.Count > 0
            ? new Vector3((float)(centerX / coordinates.Count),
                (float)(centerY / coordinates.Count), (float)(centerZ / coordinates.Count))
            : Vector3.zero;

        for (int i = 0; i < coordinates.Count; i++)
        {
            if (i == atoms.Count)
                atoms.Add(null);

            if (atoms[i] == null)
            {
                atoms[i] = Instantiate(atomPrefab, transform, false);
                atoms[i].name = $"Atom {i}";
            }

            Transform atom = atoms[i].transform;
            atom.localPosition = (coordinates[i] - center) * coordinateScale;
            atom.localRotation = Quaternion.identity;
            atom.localScale = Vector3.one * (atomSize * coordinateScale);
            atoms[i].SetActive(true);
        }

        // Retain surplus instances for later frames, without touching other children.
        for (int i = coordinates.Count; i < atoms.Count; i++)
        {
            if (atoms[i] != null)
                atoms[i].SetActive(false);
        }
    }

    // Hide all atom instances. Called here and by RepresentationController whenever a VMD
    // representation (coordinate route or imported mesh) draws the molecule instead.
    public void HideLegacyAtoms()
    {
        foreach (GameObject atom in atoms)
            if (atom != null) atom.SetActive(false);
    }

    // Return true if the value is neither NaN nor infinity.
    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // Unsubscribe from coordinate updates and reset the initial-request state.
    private void OnDisable()
    {
        if (subscribedClient != null)
            subscribedClient.CoordinatesReceived -= RenderMolecule;
        subscribedClient = null;
        requestedInitialCoordinates = false;
    }

    // Destroy all atom instances created by this renderer.
    private void OnDestroy()
    {
        foreach (GameObject atom in atoms)
        {
            if (atom != null)
                Destroy(atom);
        }
        atoms.Clear();
    }
}
