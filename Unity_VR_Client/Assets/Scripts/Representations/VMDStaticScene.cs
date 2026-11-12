using System.Collections.Generic;
using UnityEngine;

// Uploads precomputed OBJ triangles. This component has no VDW/CPK/surface/cartoon code.
//
// It is the Unity side of an imported VMD export: one child object, mesh and material per part
// that VMDObjLoader produced. RepresentationController.LoadScene adds it to a new object under
// the molecule and calls Load once. During mesh-route playback RepresentationController passes
// each coordinate frame to UpdateCoordinates, which moves the vertices through the
// VMDMeshBinding, if the export came with one. Destroying the object releases its meshes and
// materials.
public sealed class VMDStaticScene : MonoBehaviour
{
    // Everything created by Load, kept so that OnDestroy can release it.
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<Material> materials = new List<Material>();
    // The meshes that follow atoms, each with the binding part holding its current vertices.
    private readonly Dictionary<Mesh, VMDMeshBinding.Part> drivenMeshes = new Dictionary<Mesh, VMDMeshBinding.Part>();
    // Null for an export without scene.binding; the scene is then static.
    private VMDMeshBinding binding;
    // The "coordinates rejected" warning is logged once, until an update succeeds again.
    private bool warnedMismatch;
    // True when UpdateCoordinates can move at least one mesh. RepresentationController starts
    // mesh-route playback only then.
    public bool CanDeform => binding != null && drivenMeshes.Count > 0;

    // Create one child GameObject with its own Mesh and Material per OBJ part, using the
    // VMDStaticGeometry shader (transparent parts render in the transparent queue without depth
    // writes), then scale and center the whole scene under this transform.
    // 'scale' is Unity units per OBJ unit (MoleculeRenderer.CoordinateScale). 'meshBinding' may
    // be null; parts it contains become deformable.
    public void Load(VMDObjLoader.Scene scene, float scale, VMDMeshBinding meshBinding = null)
    {
        binding = meshBinding;
        Shader shader = Resources.Load<Shader>("VMDStaticGeometry");
        if (shader == null) throw new System.InvalidOperationException("VMDStaticGeometry shader is missing.");
        for (int partIndex = 0; partIndex < scene.parts.Count; partIndex++)
        {
            VMDObjLoader.Part part = scene.parts[partIndex];
            var mesh = new Mesh { name = part.group };
            meshes.Add(mesh);
            mesh.SetVertices(part.vertices);
            mesh.SetNormals(part.normals);
            mesh.SetColors(part.colors);
            // The shader reads the material data per vertex: specular color and shininess from
            // UV channel 0, ambient color from UV channel 1.
            mesh.SetUVs(0, part.specular);
            mesh.SetUVs(1, part.ambient);
            mesh.SetTriangles(part.triangles, 0);
            mesh.RecalculateBounds();
            // A bound mesh keeps its data readable for later vertex updates; a static one gives
            // up its CPU copy after the upload.
            if (binding != null && binding.parts.TryGetValue(partIndex, out VMDMeshBinding.Part driven))
            {
                mesh.MarkDynamic();
                drivenMeshes.Add(mesh, driven);
                mesh.UploadMeshData(false);
            }
            else mesh.UploadMeshData(true);
            var node = new GameObject(part.group);
            node.transform.SetParent(transform, false);
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = node.AddComponent<MeshRenderer>();
            var material = new Material(shader);
            materials.Add(material);
            // Render queue 2000 is Unity's Geometry queue, 3000 its Transparent queue.
            material.SetFloat("_ZWrite", part.transparent ? 0 : 1);
            material.renderQueue = part.transparent ? 3000 : 2000;
            renderer.sharedMaterial = material;
        }
        // VMD's view transforms are already baked into OBJ; do not apply them again.
        // Only scale the scene and put the center of its bounds on the parent's origin.
        transform.localScale = Vector3.one * scale;
        transform.localPosition = -scene.bounds.center * scale;
    }

    // Deform the bound meshes for a new coordinate frame from VMD (one position per atom).
    // Returns true when the meshes were updated. Returns false, leaving the meshes unchanged,
    // when the scene has no binding or the binding rejects the coordinates (different atom count,
    // or a value that is not finite). Called by RepresentationController.OnCoordinatesUpdated
    // during mesh-route playback.
    public bool UpdateCoordinates(IReadOnlyList<Vector3> coordinates)
    {
        if (!CanDeform) return false;
        if (!binding.UpdateCoordinates(coordinates))
        {
            if (!warnedMismatch) Debug.LogWarning("[VMD mesh] Atom count changed or invalid coordinates; keeping the mesh until a new export.", this);
            warnedMismatch = true;
            return false;
        }
        warnedMismatch = false;
        foreach (var item in drivenMeshes)
        {
            item.Key.vertices = item.Value.vertices;
            // VDW vertices only translate with their atom, so their normals stay valid.
            if (!item.Value.rigidAtoms) item.Key.RecalculateNormals();
            item.Key.RecalculateBounds();
        }
        return true;
    }

    // Release the runtime meshes and materials created by Load.
    private void OnDestroy()
    {
        foreach (Mesh mesh in meshes) if (mesh != null) Destroy(mesh);
        foreach (Material material in materials) if (material != null) Destroy(material);
    }
}
