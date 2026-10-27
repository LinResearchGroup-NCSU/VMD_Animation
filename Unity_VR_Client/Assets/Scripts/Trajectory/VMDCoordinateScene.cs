using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[Serializable] public sealed class VMDTrajectoryReply
{
    public string requestId, error, message, token;
    public bool supported, ok;
    public int molecule, atomCount, frameCount, frame, start;
    public float[] matrix;
    public VMDAtomRepresentation[] reps;
    public VMDCartoonRepresentation[] cartoons;
    public VMDQuickSurfRepresentation[] quicksurfs;
    public VMDCoordinateFrame[] frames;
}
[Serializable] public sealed class VMDCoordinateFrame { public float[] positions; }
[Serializable] public sealed class VMDAtomRepresentation
{
    public int id = -1;
    public string method;
    public int[] indices, bonds;
    public float[] radii, colors;
    public float sphereScale, bondRadius;
}

// One shared sphere/cylinder mesh, fixed-size instance batches, no per-atom GameObjects.
// Coordinates stay in a fixed molecular reference frame (no per-frame recentering).
public sealed class VMDCoordinateScene : MonoBehaviour
{
    private sealed class Batch
    {
        public readonly Matrix4x4[] matrices;
        public readonly Vector4[] colors;
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        public Batch(int count) { matrices = new Matrix4x4[count]; colors = new Vector4[count]; }
    }
    private readonly List<Batch> spheres = new List<Batch>(), cylinders = new List<Batch>();
    private VMDTrajectoryReply metadata;
    private Mesh sphere, cylinder;
    private Material material;
    private Vector3 center;
    private float scale;
    private Matrix4x4 view = Matrix4x4.identity;
    private float radiusScale = 1;
    private float[] positions;
    public Bounds WorldBounds { get; private set; }
    public bool HasFrame => positions != null;
    public int AtomInstanceCount { get; private set; }
    public int BondInstanceCount { get; private set; }

    public void Initialize(VMDTrajectoryReply source, float coordinateScale)
    {
        metadata = source;
        scale = coordinateScale;
        if (source.matrix?.Length == 16)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++) view[row,col] = source.matrix[row*4+col];
            radiusScale = view.MultiplyVector(Vector3.right).magnitude;
        }
        // A referenced instancing-enabled material retains the shader variant in player builds.
        var template = Resources.Load<Material>("VMDCoordinateInstances");
        if (template == null) throw new InvalidOperationException("VMDCoordinateInstances material is missing.");
        material = new Material(template) { enableInstancing = true };
        sphere = MakeSphere(12, 8);
        cylinder = MakeCylinder(12);
        foreach (var rep in source.reps)
        {
            if (rep.indices == null || rep.radii?.Length != rep.indices.Length || rep.colors?.Length != rep.indices.Length * 3 ||
                rep.bonds == null || rep.bonds.Length % 2 != 0) throw new FormatException("Invalid atom representation.");
            foreach (int atom in rep.indices)
                if (atom < 0 || atom >= source.atomCount) throw new FormatException("Invalid atom index.");
            foreach (int atom in rep.bonds)
                if (atom < 0 || atom >= source.atomCount) throw new FormatException("Invalid bond index.");
            AtomInstanceCount += rep.indices.Length;
            BondInstanceCount += rep.bonds.Length; // Two colored half-bonds per bond.
        }
        Allocate(spheres, AtomInstanceCount);
        Allocate(cylinders, BondInstanceCount);
        int si = 0, bi = 0;
        foreach (var rep in source.reps)
        {
            var colorByAtom = new Dictionary<int, Vector4>();
            for (int i = 0; i < rep.indices.Length; i++)
            {
                var color = new Vector4(rep.colors[i*3], rep.colors[i*3+1], rep.colors[i*3+2], 1);
                colorByAtom.Add(rep.indices[i], color);
                spheres[si / 511].colors[si++ % 511] = color;
            }
            for (int i = 0; i < rep.bonds.Length; i++)
            {
                cylinders[bi / 511].colors[bi++ % 511] = colorByAtom[rep.bonds[i]];
            }
        }
        foreach (var batch in spheres) batch.properties.SetVectorArray("_AtomColor", batch.colors);
        foreach (var batch in cylinders) batch.properties.SetVectorArray("_AtomColor", batch.colors);
    }

    private static void Allocate(List<Batch> batches, int count)
    {
        for (int i = 0; i < count; i += 511) batches.Add(new Batch(Math.Min(511, count - i)));
    }

    public void Show(float[] frame)
    {
        if (frame == null || frame.Length != metadata.atomCount * 3) throw new FormatException("Wrong coordinate count.");
        foreach (float value in frame)
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new FormatException("Non-finite coordinate.");
        if (positions == null)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < frame.Length; i += 3) { x += frame[i]; y += frame[i+1]; z -= frame[i+2]; }
            Vector3 p = view.MultiplyPoint3x4(new Vector3((float)(x / metadata.atomCount), (float)(y / metadata.atomCount), -(float)(z / metadata.atomCount)));
            center = new Vector3(p.x,p.y,-p.z);
        }
        positions = frame;
        UpdateMatrices();
    }

    private Vector3 Point(int index)
    {
        Vector3 p = view.MultiplyPoint3x4(new Vector3(positions[index*3], positions[index*3+1], positions[index*3+2]));
        return (new Vector3(p.x,p.y,-p.z) - center) * scale;
    }

    private void UpdateMatrices()
    {
        int si = 0, bi = 0;
        Matrix4x4 root = transform.localToWorldMatrix;
        Bounds bounds = new Bounds();
        bool first = true;
        foreach (var rep in metadata.reps)
        {
            for (int i = 0; i < rep.indices.Length; i++)
            {
                Vector3 p = Point(rep.indices[i]);
                float radius = rep.radii[i] * rep.sphereScale * scale * radiusScale;
                spheres[si / 511].matrices[si++ % 511] = root * Matrix4x4.TRS(p, Quaternion.identity, Vector3.one * radius);
                var atomBounds = new Bounds(p, Vector3.one * (Mathf.Max(radius, rep.bondRadius * scale * radiusScale) * 2));
                if (first) { bounds = atomBounds; first = false; } else bounds.Encapsulate(atomBounds);
            }
            for (int i = 0; i < rep.bonds.Length; i += 2)
            {
                Vector3 a = Point(rep.bonds[i]), b = Point(rep.bonds[i+1]), mid = (a + b) * 0.5f;
                SetBond(bi++, a, mid, rep.bondRadius * scale * radiusScale, root);
                SetBond(bi++, b, mid, rep.bondRadius * scale * radiusScale, root);
            }
        }
        Vector3 ext = bounds.extents;
        Vector3 x = root.MultiplyVector(new Vector3(ext.x,0,0)), y = root.MultiplyVector(new Vector3(0,ext.y,0)), z = root.MultiplyVector(new Vector3(0,0,ext.z));
        WorldBounds = new Bounds(root.MultiplyPoint3x4(bounds.center), new Vector3(
            Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x), Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y), Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z)) * 2);
        transform.hasChanged = false;
    }

    private void SetBond(int index, Vector3 a, Vector3 b, float radius, Matrix4x4 root)
    {
        Vector3 direction = b - a;
        Quaternion rotation = direction.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(Vector3.up, direction) : Quaternion.identity;
        cylinders[index / 511].matrices[index % 511] = root * Matrix4x4.TRS((a+b)*0.5f, rotation, new Vector3(radius, direction.magnitude, radius));
    }

    private void LateUpdate()
    {
        if (!HasFrame) return;
        if (transform.hasChanged) UpdateMatrices();
        // Null graphics device is used by the headless verification project.
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Draw(sphere, spheres);
        Draw(cylinder, cylinders);
    }
    private void Draw(Mesh mesh, List<Batch> batches)
    {
        foreach (var batch in batches)
        {
            var parameters = new RenderParams(material) { worldBounds = WorldBounds, matProps = batch.properties,
                layer = gameObject.layer, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            Graphics.RenderMeshInstanced(parameters, mesh, 0, batch.matrices, batch.matrices.Length);
        }
    }

    private static Mesh MakeSphere(int longitude, int latitude)
    {
        var vertices = new Vector3[(longitude+1)*(latitude+1)];
        var triangles = new List<int>();
        for (int y = 0; y <= latitude; y++)
        for (int x = 0; x <= longitude; x++)
        {
            float a = Mathf.PI*y/latitude, b = 2*Mathf.PI*x/longitude;
            vertices[y*(longitude+1)+x] = new Vector3(Mathf.Sin(a)*Mathf.Cos(b), Mathf.Cos(a), Mathf.Sin(a)*Mathf.Sin(b));
            if (x < longitude && y < latitude)
            {
                int i = y*(longitude+1)+x, j = i+longitude+1;
                triangles.AddRange(new[] {i,j+1,j,i,i+1,j+1});
            }
        }
        var mesh = new Mesh { name = "Shared VDW sphere", vertices = vertices, normals = vertices, triangles = triangles.ToArray() };
        mesh.RecalculateBounds(); return mesh;
    }
    private static Mesh MakeCylinder(int sides)
    {
        var vertices = new Vector3[(sides+1)*2];
        var normals = new Vector3[vertices.Length];
        var triangles = new List<int>();
        for (int i = 0; i <= sides; i++)
        {
            float a = i*2*Mathf.PI/sides;
            Vector3 n = new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));
            vertices[2*i] = n + Vector3.down*0.5f; vertices[2*i+1] = n + Vector3.up*0.5f;
            normals[2*i] = normals[2*i+1] = n;
            if (i < sides) triangles.AddRange(new[] {2*i,2*i+1,2*i+3,2*i,2*i+3,2*i+2});
        }
        var mesh = new Mesh { name = "Shared CPK bond", vertices = vertices, normals = normals, triangles = triangles.ToArray() };
        mesh.RecalculateBounds(); return mesh;
    }
    private void OnDestroy() { Release(sphere); Release(cylinder); Release(material); }
    private static void Release(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }
}
