using System.Collections.Generic;
using UnityEngine;

// Uploads precomputed OBJ triangles. This component has no VDW/CPK/surface/cartoon code.
public sealed class VMDStaticScene : MonoBehaviour
{
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<Material> materials = new List<Material>();
    private readonly Dictionary<Mesh, VMDMeshBinding.Part> drivenMeshes = new Dictionary<Mesh, VMDMeshBinding.Part>();
    private VMDMeshBinding binding;
    private bool warnedMismatch;
    public bool CanDeform => binding != null && drivenMeshes.Count > 0;

    // Create one child GameObject with its own Mesh and Material per OBJ part, using the
    // VMDStaticGeometry shader (transparent parts render in the transparent queue without depth
    // writes), then scale and center the whole scene under this transform.
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
            mesh.SetUVs(0, part.specular);
            mesh.SetUVs(1, part.ambient);
            mesh.SetTriangles(part.triangles, 0);
            mesh.RecalculateBounds();
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
            material.SetFloat("_ZWrite", part.transparent ? 0 : 1);
            material.renderQueue = part.transparent ? 3000 : 2000;
            renderer.sharedMaterial = material;
        }
        // VMD's view transforms are already baked into OBJ; do not apply them again.
        transform.localScale = Vector3.one * scale;
        transform.localPosition = -scene.bounds.center * scale;
    }

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
