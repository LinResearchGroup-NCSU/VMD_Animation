using System.Collections.Generic;
using UnityEngine;

// Uploads precomputed OBJ triangles. This component has no VDW/CPK/surface/cartoon code.
public sealed class VMDStaticScene : MonoBehaviour
{
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<Material> materials = new List<Material>();

    public void Load(VMDObjLoader.Scene scene, float scale)
    {
        Shader shader = Resources.Load<Shader>("VMDStaticGeometry");
        if (shader == null) throw new System.InvalidOperationException("VMDStaticGeometry shader is missing.");
        foreach (VMDObjLoader.Part part in scene.parts)
        {
            var mesh = new Mesh { name = part.group };
            meshes.Add(mesh);
            mesh.SetVertices(part.vertices);
            mesh.SetNormals(part.normals);
            mesh.SetColors(part.colors);
            mesh.SetUVs(0, part.specular);
            mesh.SetUVs(1, part.ambient);
            mesh.SetTriangles(part.triangles, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
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

    private void OnDestroy()
    {
        foreach (Mesh mesh in meshes) if (mesh != null) Destroy(mesh);
        foreach (Material material in materials) if (material != null) Destroy(material);
    }
}
