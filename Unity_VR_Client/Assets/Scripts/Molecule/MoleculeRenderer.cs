using UnityEngine;
using System.Collections.Generic;

// Render VMD atom coordinates in this object's local coordinate space.
public class MoleculeRenderer : MonoBehaviour
{
    public GameObject atomPrefab; // Prefab for rendering atoms
    [SerializeField] private VMDClient client;
    // May need to change the scale and atom size to match the VMD scene, e.g. if the molecule is very large or very small.
    [SerializeField, Min(0.0001f)] private float coordinateScale = 1f;
    // Diameter in VMD coordinate units; scaled together with atom positions.
    [SerializeField, Min(0.0001f)] private float atomSize = 1.5f;

    private readonly List<GameObject> atoms = new List<GameObject>();
    private VMDClient subscribedClient;
    private bool requestedInitialCoordinates;
    private RepresentationController representationController;
    public float CoordinateScale => coordinateScale;

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

    private void Update()
    {
        if (subscribedClient == null)
            return;

        if (!subscribedClient.IsConnected)
        {
            requestedInitialCoordinates = false;
            return;
        }

        // Wait for the asynchronous connection before requesting the first frame.
        if (!requestedInitialCoordinates)
        {
            requestedInitialCoordinates = true;
            subscribedClient.RequestCoordinates();
        }
    }

    public void RenderMolecule(IReadOnlyList<Vector3> coordinates)
    {
        if (coordinates == null)
        {
            Debug.LogWarning("[MoleculeRenderer] Coordinates cannot be null.", this);
            return;
        }

        bool refit = representationController != null && representationController.OnCoordinatesUpdated();

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
        if (refit) representationController.FitView();
    }

    public void HideLegacyAtoms()
    {
        foreach (GameObject atom in atoms)
            if (atom != null) atom.SetActive(false);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnDisable()
    {
        if (subscribedClient != null)
            subscribedClient.CoordinatesReceived -= RenderMolecule;
        subscribedClient = null;
        requestedInitialCoordinates = false;
    }

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
