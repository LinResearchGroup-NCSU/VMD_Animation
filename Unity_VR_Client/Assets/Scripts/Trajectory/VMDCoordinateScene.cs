using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// JSON payload of a TRAJJSON line, the reply to TRAJ_OPEN, TRAJ_READ, TRAJ_CHECK,
// TRAJ_SYNC and TRAJ_STYLE. Each reply fills only some fields. The field names are
// those written by VMD_VR_Bridge/trajectory.tcl, cartoon.tcl and quicksurf.tcl.
[Serializable] public sealed class VMDTrajectoryReply
{
    // error: set when the command failed; VMDClient.RequestTrajectory throws it.
    // message: why TRAJ_OPEN answered supported = false.
    // token: identifies the snapshot made by TRAJ_OPEN;
    //   TRAJ_READ, TRAJ_CHECK and TRAJ_SYNC send it back.
    public string requestId, error, message, token;
    // supported: TRAJ_OPEN only; the coordinate route can draw the visible scene.
    // ok: set by the successful replies to the other commands.
    public bool supported, ok;
    // molecule: VMD molecule ID. frame: the frame VMD displays (TRAJ_OPEN and TRAJ_CHECK).
    // start: first frame of a TRAJ_READ batch.
    public int molecule, atomCount, frameCount, frame, start;
    // VMD's transform of the molecule at TRAJ_OPEN (global * scale
    // * rotate * center matrices), as 16 values in row-major order.
    public float[] matrix;
    // The visible reps, one array per renderer: VDW and CPK, NewCartoon, QuickSurf.
    public VMDAtomRepresentation[] reps;
    public VMDCartoonRepresentation[] cartoons;
    public VMDQuickSurfRepresentation[] quicksurfs;
    // TRAJ_READ only: consecutive frames, the first one being frame 'start'.
    public VMDCoordinateFrame[] frames;
}
// One frame: x, y, z of every atom of the molecule, in VMD coordinates (atomCount * 3 values).
[Serializable] public sealed class VMDCoordinateFrame { public float[] positions; }
// One visible VDW or CPK rep. 'id' is the VMD rep index and 'method' is "VDW" or "CPK".
[Serializable] public sealed class VMDAtomRepresentation
{
    public int id = -1;
    public string method;
    // indices: the selected atoms. bonds: pairs of atom indices, both selected; empty for VDW.
    public int[] indices, bonds;
    // Per selected atom: its VMD radius, and its color as r, g, b.
    public float[] radii, colors;
    // sphereScale multiplies the radii; bondRadius is the bond cylinder radius, 0 for VDW.
    // For CPK the bridge sends a quarter of the sphere and bond values of VMD's style.
    public float sphereScale, bondRadius;
}

// Draws the VDW and CPK reps of the coordinate route with GPU instancing: one shared
// sphere mesh, one shared cylinder mesh, fixed-size instance batches, no per-atom
// GameObjects. Coordinates stay in a fixed molecular reference frame: the center is
// taken from the first frame and kept, so the molecule is not recentered every frame.
//
// TrajectoryController creates this component on a child object of the
// MoleculeRenderer, calls Initialize with the TRAJ_OPEN reply and Show for every
// frame it displays. The component is created even when the snapshot has no VDW
// or CPK rep (it then draws nothing), because TrajectoryController uses HasFrame
// to tell whether the first frame is on display. VMDHighlightController calls
// SetHighlight, VMDCameraController reads WorldBounds to fit the view, and VMDRayHighlight
// calls TryPickAtom to find the atom a controller ray points at.
public sealed class VMDCoordinateScene : MonoBehaviour
{
    // Instances are drawn in batches of at most 511, the batch size VMDCoordinateInstances.shader
    // is compiled for (forcemaxcount:511). Instance i is slot i % 511 of batch i / 511. A batch
    // holds the world matrices and the per-instance colors of its instances.
    private sealed class Batch
    {
        public readonly Matrix4x4[] matrices;
        public readonly Vector4[] colors;
        public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        public Batch(int count) { matrices = new Matrix4x4[count]; colors = new Vector4[count]; }
    }
    // One sphere instance per selected atom, and two half-bond cylinder instances per bond.
    private readonly List<Batch> spheres = new List<Batch>(), cylinders = new List<Batch>();
    // The TRAJ_OPEN reply; its reps are walked again for every frame.
    private VMDTrajectoryReply metadata;
    // Shared meshes: a sphere of radius 1, and an open cylinder of radius 1 and height 1 along Y.
    private Mesh sphere, cylinder;
    private Material material;
    // Center of the first frame, after 'view' and the Z flip; subtracted from every frame.
    private Vector3 center;
    // Scale from VMD coordinates to this object's local space (MoleculeRenderer.CoordinateScale).
    private float scale;
    // 'view' is VMD's molecule transform from the TRAJ_OPEN reply. radiusScale is the length
    // to which it maps the unit X vector; sphere and bond radii are multiplied by it.
    private Matrix4x4 view = Matrix4x4.identity;
    private float radiusScale = 1;
    // The frame on display; null before the first Show.
    private float[] positions;
    // Atom and original color of every sphere and half-bond instance, in batch order, for highlighting.
    private int[] sphereAtoms, cylinderAtoms;
    private Vector4[] sphereColors, cylinderColors;
    // World-space bounds of all instances, used for culling and by VMDCameraController.
    public Bounds WorldBounds { get; private set; }
    // True once Show has displayed a frame.
    public bool HasFrame => positions != null;
    public int AtomCount => metadata?.atomCount ?? 0;
    // Numbers of sphere instances and of half-bond cylinder instances.
    public int AtomInstanceCount { get; private set; }
    public int BondInstanceCount { get; private set; }

    // Read the snapshot once: the view matrix, the shared meshes and material,
    // and for every rep its validated atoms, bonds and colors. The instance
    // matrices follow in Show. Throws when a resource is missing or the
    // metadata is invalid; TrajectoryController reports that as a failure.
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
        // Validate every rep and count its instances.
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
        sphereAtoms = new int[AtomInstanceCount]; sphereColors = new Vector4[AtomInstanceCount];
        cylinderAtoms = new int[BondInstanceCount]; cylinderColors = new Vector4[BondInstanceCount];
        // Store the instance colors. A half-bond has the color of the atom it starts from.
        int si = 0, bi = 0;
        foreach (var rep in source.reps)
        {
            var colorByAtom = new Dictionary<int, Vector4>();
            for (int i = 0; i < rep.indices.Length; i++)
            {
                var color = new Vector4(rep.colors[i*3], rep.colors[i*3+1], rep.colors[i*3+2], 1);
                colorByAtom.Add(rep.indices[i], color);
                sphereAtoms[si] = rep.indices[i]; sphereColors[si] = color;
                spheres[si / 511].colors[si++ % 511] = color;
            }
            for (int i = 0; i < rep.bonds.Length; i++)
            {
                cylinderAtoms[bi] = rep.bonds[i]; cylinderColors[bi] = colorByAtom[rep.bonds[i]];
                cylinders[bi / 511].colors[bi++ % 511] = colorByAtom[rep.bonds[i]];
            }
        }
        foreach (var batch in spheres) batch.properties.SetVectorArray("_AtomColor", batch.colors);
        foreach (var batch in cylinders) batch.properties.SetVectorArray("_AtomColor", batch.colors);
        // Drawn sphere radius per atom, in local units, for TryPickAtom; the largest when two
        // reps draw the same atom.
        sphereRadius = new float[source.atomCount];
        foreach (var rep in source.reps)
            for (int i = 0; i < rep.indices.Length; i++)
                sphereRadius[rep.indices[i]] = Mathf.Max(sphereRadius[rep.indices[i]],
                    rep.radii[i] * rep.sphereScale * scale * radiusScale);
    }

    // Per atom index, the radius of its drawn sphere in local units, 0 when no VDW or CPK rep
    // draws it. Set by Initialize.
    private float[] sphereRadius;

    // Mute every sphere and half-bond whose atom is not in 'selected';
    // null restores VMD's colors. Called by VMDHighlightController.
    public void SetHighlight(bool[] selected, float dimming)
    {
        if (sphereAtoms == null) return;
        Recolor(spheres, sphereAtoms, sphereColors, selected, dimming);
        Recolor(cylinders, cylinderAtoms, cylinderColors, selected, dimming);
    }

    // Set the instance colors of 'batches' from 'original',
    // dimming unselected atoms, and pass them to the shader.
    private static void Recolor(List<Batch> batches, int[] atoms, Vector4[] original, bool[] selected, float dimming)
    {
        for (int i = 0; i < atoms.Length; i++)
            batches[i / 511].colors[i % 511] = VMDHighlightController.IsSelected(selected, atoms[i])
                ? original[i] : (Vector4)VMDHighlightController.Dim(original[i], dimming);
        foreach (var batch in batches) batch.properties.SetVectorArray("_AtomColor", batch.colors);
    }

    // Create the batches for 'count' instances.
    private static void Allocate(List<Batch> batches, int count)
    {
        for (int i = 0; i < count; i += 511) batches.Add(new Batch(Math.Min(511, count - i)));
    }

    // Display one frame: x, y, z per atom, in VMD coordinates. The first frame also
    // fixes the center. Throws FormatException for a wrong length or a non-finite value.
    public void Show(float[] frame)
    {
        if (frame == null || frame.Length != metadata.atomCount * 3) throw new FormatException("Wrong coordinate count.");
        foreach (float value in frame)
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new FormatException("Non-finite coordinate.");
        if (positions == null)
        {
            // First frame: the center is the mean atom
            // position, mapped like every point (see Point).
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < frame.Length; i += 3) { x += frame[i]; y += frame[i+1]; z -= frame[i+2]; }
            Vector3 p = view.MultiplyPoint3x4(new Vector3((float)(x / metadata.atomCount), (float)(y / metadata.atomCount), -(float)(z / metadata.atomCount)));
            center = new Vector3(p.x,p.y,-p.z);
        }
        positions = frame;
        UpdateMatrices();
    }

    // The atom of 'atoms' (atom indices) that a world-space ray meets first in the current frame,
    // and how far along the ray, in world units, it meets it. Every atom is a sphere:
    // - an atom drawn here as a VDW or CPK sphere uses that sphere's radius, so the pick matches
    //   what is drawn; a sphere that contains the ray origin (the hand) is skipped, so the ray
    //   picks what it points at rather than what the hand is in;
    // - any other atom (NewCartoon, QuickSurf) uses 'maxDistance' angstroms around its center.
    // All live renderers share this object and its coordinate mapping, so every atom of the frame
    // can be picked here. False before the first frame or when the ray meets none.
    // Called by VMDRayHighlight.
    public bool TryPickAtom(Ray worldRay, int[] atoms, float maxDistance, out int atom, out float distance)
    {
        atom = -1;
        distance = float.PositiveInfinity;
        if (positions == null || atoms == null)
            return false;
        Matrix4x4 toLocal = transform.worldToLocalMatrix;
        Vector3 origin = toLocal.MultiplyPoint3x4(worldRay.origin);
        Vector3 direction = toLocal.MultiplyVector(worldRay.direction);
        if (direction.sqrMagnitude < 1e-12f)
            return false;
        direction.Normalize();
        // Angstroms to local units: VMD's view matrix scales by radiusScale, then 'scale' applies.
        float fallback = maxDistance * radiusScale * scale, nearest = float.PositiveInfinity;
        foreach (int index in atoms)
        {
            if (index < 0 || index >= metadata.atomCount)
                continue;
            float radius = sphereRadius != null && sphereRadius[index] > 0f ? sphereRadius[index] : fallback;
            Vector3 offset = Point(index) - origin;
            float along = Vector3.Dot(offset, direction);
            float gap = radius * radius - (offset.sqrMagnitude - along * along);
            if (gap < 0f)
                continue;
            // Where the ray enters the sphere; negative when the origin is inside or past it.
            float enter = along - Mathf.Sqrt(gap);
            if (enter < 0f || enter >= nearest)
                continue;
            nearest = enter;
            atom = index;
        }
        if (atom < 0)
            return false;
        distance = transform.TransformVector(direction * nearest).magnitude;
        return true;
    }

    // Atom 'index' of the current frame in this object's local space: apply VMD's view
    // matrix, flip Z (VMD is right-handed, Unity left-handed), subtract the center and scale.
    private Vector3 Point(int index)
    {
        Vector3 p = view.MultiplyPoint3x4(new Vector3(positions[index*3], positions[index*3+1], positions[index*3+2]));
        return (new Vector3(p.x,p.y,-p.z) - center) * scale;
    }

    // Recompute every instance matrix and WorldBounds from the current frame and
    // this transform. The matrices are in world space, as RenderMeshInstanced
    // expects. Called by Show, and by LateUpdate when the transform has changed.
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
                // The bounds allow for the larger of the
                // sphere and the bond radius around each atom.
                var atomBounds = new Bounds(p, Vector3.one * (Mathf.Max(radius, rep.bondRadius * scale * radiusScale) * 2));
                if (first) { bounds = atomBounds; first = false; } else bounds.Encapsulate(atomBounds);
            }
            for (int i = 0; i < rep.bonds.Length; i += 2)
            {
                // A bond is two half-cylinders, one from each
                // atom to the midpoint, each in its atom's color.
                Vector3 a = Point(rep.bonds[i]), b = Point(rep.bonds[i+1]), mid = (a + b) * 0.5f;
                SetBond(bi++, a, mid, rep.bondRadius * scale * radiusScale, root);
                SetBond(bi++, b, mid, rep.bondRadius * scale * radiusScale, root);
            }
        }
        // World-space box that encloses the local bounds under this transform.
        Vector3 ext = bounds.extents;
        Vector3 x = root.MultiplyVector(new Vector3(ext.x,0,0)), y = root.MultiplyVector(new Vector3(0,ext.y,0)), z = root.MultiplyVector(new Vector3(0,0,ext.z));
        WorldBounds = new Bounds(root.MultiplyPoint3x4(bounds.center), new Vector3(
            Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x), Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y), Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z)) * 2);
        transform.hasChanged = false;
    }

    // Place cylinder instance 'index' from a to b: the unit cylinder is scaled to the bond radius
    // and the segment length, turned from the Y axis to the segment direction and centered on it.
    private void SetBond(int index, Vector3 a, Vector3 b, float radius, Matrix4x4 root)
    {
        Vector3 direction = b - a;
        Quaternion rotation = direction.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(Vector3.up, direction) : Quaternion.identity;
        cylinders[index / 511].matrices[index % 511] = root * Matrix4x4.TRS((a+b)*0.5f, rotation, new Vector3(radius, direction.magnitude, radius));
    }

    // Submit the instances for this frame, after updating
    // the matrices if the transform has changed.
    private void LateUpdate()
    {
        if (!HasFrame) return;
        if (transform.hasChanged) UpdateMatrices();
        // Null graphics device is used by the headless verification project.
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Draw(sphere, spheres);
        Draw(cylinder, cylinders);
    }

    // One instanced draw call per batch, without shadows.
    private void Draw(Mesh mesh, List<Batch> batches)
    {
        foreach (var batch in batches)
        {
            var parameters = new RenderParams(material) { worldBounds = WorldBounds, matProps = batch.properties,
                layer = gameObject.layer, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            Graphics.RenderMeshInstanced(parameters, mesh, 0, batch.matrices, batch.matrices.Length);
        }
    }

    // Unit sphere as a latitude/longitude grid. The vertex positions are also the normals.
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

    // Open cylinder (no caps) of radius 1 around the Y axis, from y = -0.5 to y = 0.5.
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

    // The meshes and the material were created by Initialize and are destroyed with the component.
    private void OnDestroy() { Release(sphere); Release(cylinder); Release(material); }
    // Outside Play mode Unity requires DestroyImmediate instead of Destroy.
    private static void Release(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }
}
