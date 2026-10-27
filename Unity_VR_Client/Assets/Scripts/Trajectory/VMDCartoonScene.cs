using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[Serializable] public sealed class VMDCartoonRepresentation
{
    public float radius, aspect;
    public int id, resolution, spline;
    public VMDCartoonChain[] chains;
}
[Serializable] public sealed class VMDCartoonChain
{
    public int[] indices, carbons, oxygens, structures;
    public float[] colors;
}

// A protein cartoon is reconstructed from each frame's backbone, never deformed
// from an OBJ. Pause, Seek and Play all call the same Show method and quality level.
public sealed class VMDCartoonScene : MonoBehaviour
{
    private readonly List<Strip> strips = new List<Strip>();
    private readonly List<Material> materials = new List<Material>();
    private Matrix4x4 view = Matrix4x4.identity;
    private Vector3 center;
    private float scale, viewScale = 1;
    private int atomCount;
    private bool centered;
    public int StripCount => strips.Count;

    public void Initialize(VMDTrajectoryReply metadata, float coordinateScale)
    {
        atomCount = metadata.atomCount;
        scale = coordinateScale;
        if (metadata.matrix?.Length == 16)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++) view[r,c] = metadata.matrix[r*4+c];
            viewScale = view.MultiplyVector(Vector3.right).magnitude;
        }
        var shader = Resources.Load<Shader>("VMDNewCartoon");
        if (shader == null) throw new InvalidOperationException("VMDNewCartoon shader is missing.");
        // Lighting is shared by all reps (VMDLighting.cginc), so one material serves every strip.
        var material = new Material(shader) { name = "NewCartoon" };
        materials.Add(material);
        foreach (var rep in metadata.cartoons)
        {
            if (rep.chains == null || !Finite(rep.radius) || rep.radius < 0 || !Finite(rep.aspect) || rep.aspect <= 0 ||
                rep.spline < 0 || rep.spline > 1) throw new FormatException("Invalid NewCartoon metadata.");
            foreach (var chain in rep.chains)
            {
                Validate(chain);
                if (chain.indices.Length < 2 || rep.radius == 0) continue;
                var child = new GameObject("NewCartoon backbone strip");
                child.transform.SetParent(transform, false);
                child.layer = gameObject.layer;
                strips.Add(new Strip(child, chain, rep, material, scale * viewScale));
            }
        }
    }
    private void Validate(VMDCartoonChain chain)
    {
        int n = chain?.indices?.Length ?? -1;
        if (n < 0 || chain.carbons?.Length != n || chain.oxygens?.Length != n || chain.structures?.Length != n || chain.colors?.Length != n*3)
            throw new FormatException("Invalid NewCartoon backbone.");
        for (int i = 0; i < n; i++)
        {
            if (chain.indices[i] < 0 || chain.indices[i] >= atomCount || chain.carbons[i] < -1 || chain.carbons[i] >= atomCount ||
                chain.oxygens[i] < -1 || chain.oxygens[i] >= atomCount || chain.structures[i] < 0 || chain.structures[i] > 2)
                throw new FormatException("Invalid NewCartoon atom index/structure.");
        }
    }
    public void Show(float[] positions)
    {
        if (positions == null || positions.Length != atomCount*3) throw new FormatException("Wrong cartoon coordinate count.");
        if (!centered)
        {
            // Same fixed reference center/handedness as the existing atom renderer.
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < positions.Length; i += 3) { x += positions[i]; y += positions[i+1]; z += positions[i+2]; }
            Vector3 p = view.MultiplyPoint3x4(new Vector3((float)(x/atomCount),(float)(y/atomCount),(float)(z/atomCount)));
            center = new Vector3(p.x,p.y,-p.z);
            centered = true;
        }
        foreach (var strip in strips) strip.Update(positions, this);
    }
    private Vector3 Point(float[] p, int atom)
    {
        Vector3 raw = new Vector3(p[atom*3],p[atom*3+1],p[atom*3+2]);
        if (!Finite(raw.x) || !Finite(raw.y) || !Finite(raw.z)) throw new FormatException("Non-finite backbone coordinate.");
        Vector3 mapped = view.MultiplyPoint3x4(raw);
        return (new Vector3(mapped.x,mapped.y,-mapped.z)-center)*scale;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static Vector3 Unit(Vector3 value, Vector3 fallback) => value.sqrMagnitude > 1e-12f ? value.normalized : fallback;
    private static Vector3 Perpendicular(Vector3 tangent)
    {
        Vector3 axis = Mathf.Abs(tangent.y) < 0.8f ? Vector3.up : Vector3.right;
        return Vector3.Cross(tangent,axis).normalized;
    }
    // Explicit release also works when the controller is exercised outside Play mode.
    public void Clear()
    {
        foreach (var strip in strips) Release(strip.mesh);
        foreach (var material in materials) Release(material);
        strips.Clear(); materials.Clear(); centered = false;
    }
    private void OnDestroy() => Clear();
    private static void Release(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }

    private sealed class Strip
    {
        public readonly Mesh mesh;
        private readonly VMDCartoonChain chain;
        private readonly int steps, sides, rings, spline;
        private readonly float radius, aspect, gapSquared;
        private readonly Vector3[] points, control, guides, samples, vertices;
        private readonly float[] cos, sin;
        private readonly bool[] connected;
        private readonly int[] triangles;
        private bool topologyInitialized;

        public Strip(GameObject owner, VMDCartoonChain data, VMDCartoonRepresentation rep, Material material, float scale)
        {
            chain = data;
            int n = data.indices.Length;
            steps = Mathf.Clamp(rep.resolution,4,24);
            sides = Mathf.Clamp(rep.resolution,8,24);
            spline = rep.spline;
            radius = rep.radius*scale;
            aspect = Mathf.Max(1,rep.aspect);
            gapSquared = 64*scale*scale; // Avoid bridging periodic jumps or missing backbone spans (>8 Angstrom).
            rings = (n-1)*steps+1;
            points = new Vector3[n]; control = new Vector3[n]; guides = new Vector3[n];
            samples = new Vector3[rings]; vertices = new Vector3[rings*sides+2];
            connected = new bool[n-1];
            triangles = new int[((rings-1)*sides*2+sides*2)*3];
            cos = new float[sides]; sin = new float[sides];
            for (int j = 0; j < sides; j++) { cos[j] = Mathf.Cos(j*2*Mathf.PI/sides); sin[j] = Mathf.Sin(j*2*Mathf.PI/sides); }
            var colors = new Color[vertices.Length];
            for (int ring = 0; ring < rings; ring++)
            {
                int a = Math.Min(ring/steps,n-2);
                float t = (ring-a*steps)/(float)steps;
                Color c = Color.Lerp(ColorAt(a),ColorAt(a+1),t);
                for (int j = 0; j < sides; j++) colors[ring*sides+j] = c;
            }
            colors[colors.Length-2] = ColorAt(0); colors[colors.Length-1] = ColorAt(n-1);
            mesh = new Mesh { name = "Live NewCartoon ribbon", indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.MarkDynamic(); mesh.vertices = vertices; mesh.colors = colors;
            owner.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = owner.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        private Color ColorAt(int i) => new Color(chain.colors[3*i],chain.colors[3*i+1],chain.colors[3*i+2],1);

        public void Update(float[] coordinates, VMDCartoonScene owner)
        {
            int n = points.Length;
            bool topologyChanged = !topologyInitialized;
            for (int i = 0; i < n; i++) points[i] = owner.Point(coordinates,chain.indices[i]);
            for (int i = 0; i < n-1; i++)
            {
                float distance = (points[i+1]-points[i]).sqrMagnitude;
                bool join = distance > 1e-12f && distance <= gapSquared;
                topologyChanged |= connected[i] != join;
                connected[i] = join;
            }
            for (int i = 0; i < n; i++)
            {
                // Flatten beta zigzags while preserving coil and helix centerlines.
                control[i] = chain.structures[i] == 2 && i > 0 && i+1 < n && connected[i-1] && connected[i]
                    ? (points[i-1]+points[i]*2+points[i+1])*0.25f : points[i];
                Vector3 tangent = Unit(points[Math.Min(i+1,n-1)]-points[Math.Max(i-1,0)],Vector3.forward);
                Vector3 direction = chain.carbons[i] >= 0 && chain.oxygens[i] >= 0
                    ? owner.Point(coordinates,chain.oxygens[i])-owner.Point(coordinates,chain.carbons[i]) : Vector3.zero;
                Vector3 projected = Vector3.ProjectOnPlane(direction,tangent);
                Vector3 fallback = i > 0 && connected[i-1] ? Unit(Vector3.ProjectOnPlane(guides[i-1],tangent),Perpendicular(tangent)) : Perpendicular(tangent);
                guides[i] = Unit(projected,fallback);
                if (i > 0 && connected[i-1] && Vector3.Dot(guides[i],guides[i-1]) < 0) guides[i] = -guides[i];
            }
            for (int ring = 0; ring < rings; ring++)
            {
                int a = Math.Min(ring/steps,n-2);
                float t = (ring-a*steps)/(float)steps;
                Vector3 p0 = a > 0 && connected[a-1] ? control[a-1] : control[a];
                Vector3 p3 = a+2 < n && connected[a+1] ? control[a+2] : control[a+1];
                samples[ring] = connected[a] ? Spline(p0,control[a],control[a+1],p3,t,spline) : Vector3.Lerp(control[a],control[a+1],t);
            }
            Vector3 previousSide = Vector3.right;
            for (int ring = 0; ring < rings; ring++)
            {
                int a = Math.Min(ring/steps,n-2);
                float t = (ring-a*steps)/(float)steps;
                Vector3 tangent = Unit(samples[Math.Min(ring+1,rings-1)]-samples[Math.Max(ring-1,0)],Vector3.forward);
                Vector3 fallback = Unit(Vector3.ProjectOnPlane(previousSide,tangent),Perpendicular(tangent));
                Vector3 side = Unit(Vector3.ProjectOnPlane(Vector3.Lerp(guides[a],guides[a+1],t),tangent),fallback);
                if (ring > 0 && Vector3.Dot(side,previousSide) < 0) side = -side;
                Vector3 normal = Unit(Vector3.Cross(tangent,side),Perpendicular(tangent));
                float width = radius*Mathf.Lerp(Width(a),Width(a+1),t*t*(3-2*t));
                for (int j = 0; j < sides; j++) vertices[ring*sides+j] = samples[ring]+side*(cos[j]*width)+normal*(sin[j]*radius);
                previousSide = side;
            }
            vertices[vertices.Length-2] = samples[0]; vertices[vertices.Length-1] = samples[rings-1];
            mesh.vertices = vertices;
            if (topologyChanged) { UpdateTriangles(); mesh.triangles = triangles; topologyInitialized = true; }
            // Recompute the actual surface normals, including tapering arrows, every source frame.
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
        }
        private float Width(int i)
        {
            if (chain.structures[i] == 0) return 1;
            if (chain.structures[i] == 2)
            {
                bool end = i == chain.structures.Length-1 || !connected[i] || chain.structures[i+1] != 2;
                if (end) return 0.15f;
                bool beforeTip = i+2 == chain.structures.Length || !connected[i+1] || chain.structures[i+2] != 2;
                if (beforeTip) return aspect*1.5f;
            }
            return aspect;
        }
        private void UpdateTriangles()
        {
            int k = 0;
            for (int ring = 0; ring < rings-1; ring++)
            for (int j = 0; j < sides; j++)
            {
                int a = ring*sides+j, b = ring*sides+(j+1)%sides, c = a+sides, d = b+sides;
                bool visible = connected[Math.Min(ring/steps,connected.Length-1)];
                triangles[k++] = a; triangles[k++] = visible ? b : a; triangles[k++] = visible ? c : a;
                triangles[k++] = b; triangles[k++] = visible ? d : b; triangles[k++] = visible ? c : b;
            }
            for (int j = 0; j < sides; j++)
            {
                triangles[k++] = vertices.Length-2; triangles[k++] = (j+1)%sides; triangles[k++] = j;
                triangles[k++] = vertices.Length-1; triangles[k++] = (rings-1)*sides+j; triangles[k++] = (rings-1)*sides+(j+1)%sides;
            }
        }
        // 0: Catmull-Rom; 1: uniform cubic B-spline, matching the VMD style selector.
        private static Vector3 Spline(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t, int type)
        {
            float t2 = t*t, t3 = t2*t;
            if (type == 1) return (a*(-t3+3*t2-3*t+1)+b*(3*t3-6*t2+4)+c*(-3*t3+3*t2+3*t+1)+d*t3)/6;
            return (b*2+(-a+c)*t+(a*2-b*5+c*4-d)*t2+(-a+b*3-c*3+d)*t3)*0.5f;
        }
    }
}
