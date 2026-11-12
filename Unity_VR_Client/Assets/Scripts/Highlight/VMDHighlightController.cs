using System;
using System.Threading;
using UnityEngine;

// JSON payload of the bridge's SELJSON reply to SELECT (see VMDClient.RequestSelection).
// - error: set instead of the other fields when the selection could not be resolved.
// - label: the bridge's selection name in upper case, or "" when VMD selection text was sent.
// - molecule: ID of VMD's top molecule.
// - atomCount: number of atoms in that molecule, not the number of selected atoms.
// - indices: atom indices of the selected atoms.
[Serializable] public sealed class VMDSelectionReply
{
    public string requestId, error, label;
    public bool ok;
    public int molecule, atomCount;
    public int[] indices;
}

// Highlights a VMD selection without changing VMD: the bridge resolves it to atom indices (SELECT),
// and the live renderers (VDW/CPK, NewCartoon, QuickSurf) mute every other atom's color.
// VMD's representations and the trajectory snapshot stay untouched, so nothing reloads, and
// playback, frame sync and XR placement are unaffected. Clearing restores VMD's exact colors.
// The VMD mesh route (other drawing methods) is not highlighted.
//
// Callers:
// - Cycle: the H key (VMDKeyboardController). It adds this component at runtime when the scene
//   has none; VMDStatusPanel does the same in XR modes.
// - PrepareStructures, StructureOf, ShowStructure, ActiveStructure: VMDRayHighlight, which in
//   XR modes highlights the entry of 'selections' that the right controller's ray points at.
// - Status: shown on the XR status panel.
// - IsSelected and Dim: the three live renderers, VMDCoordinateScene, VMDCartoonScene and
//   VMDQuickSurfScene, when they recolor their atoms in SetHighlight.
public class VMDHighlightController : MonoBehaviour
{
    // Both are found in the scene when left empty (Awake).
    [SerializeField] private VMDClient client;
    [SerializeField] private MoleculeRenderer molecule;
    [Tooltip("Bridge selection names (see ::vrbridge::define_selection) or VMD selection text, in Cycle order.")]
    [SerializeField] private string[] selections = { "N_DOMAIN", "C_DOMAIN", "LINKER" };
    [Tooltip("How strongly atoms outside the selection are muted: 0 unchanged, 1 dark gray.")]
    [SerializeField, Range(0f, 1f)] private float dimming = 0.75f;

    // One flag per atom of VMD's top molecule, indexed by atom index: true for selected atoms.
    // Null while no highlight is active.
    private bool[] mask;
    // Status text of the active highlight, kept so that Apply can rebuild Status from it.
    private string summary = "";
    // cycleIndex: entry of 'selections' that Cycle chose last, -1 when off.
    // request: incremented by every Highlight and Clear, so the reply to an older request is
    // dropped when it arrives.
    private int cycleIndex = -1, request;
    // Connection state in the previous frame, to notice the moment the connection drops.
    private bool wasConnected;
    // Cancels a pending selection request when the component is disabled.
    private CancellationTokenSource lifetime;
    // The live renderers the highlight was last applied to. LateUpdate compares them with the
    // molecule's current ones to notice that a renderer was rebuilt.
    private VMDCoordinateScene coordinateScene;
    private VMDCartoonScene cartoonScene;
    private VMDQuickSurfScene quickSurfScene;

    // Structures for ray highlighting: every entry of 'selections', resolved once per connection,
    // so pointing at another one switches the highlight without a bridge request.
    // structureOfAtom: per atom index, the entry that contains the atom (the first one when
    // entries overlap), -1 for none. structureAtoms: the atoms that belong to some entry.
    // structureMasks, structureLabels, structureSizes: per entry, its mask, name and atom count.
    // All null until resolved.
    private int[] structureOfAtom, structureAtoms, structureSizes;
    private bool[][] structureMasks;
    private string[] structureLabels;
    // A resolve is running; or it failed and is not retried until the connection drops.
    private bool resolving, resolveFailed;
    // Incremented by DropStructures, so a resolve that is still running discards its result.
    private int resolveVersion;

    // The highlighted selection ("" when off) and one status line; VMDStatusPanel shows Status.
    public string Active { get; private set; } = "";
    public string Status { get; private set; } = "";
    // Entry of 'selections' shown by ShowStructure, -1 when the highlight came from Cycle or
    // Highlight, or is off.
    public int ActiveStructure { get; private set; } = -1;

    // A null selection means no highlight, so every atom keeps its color.
    public static bool IsSelected(bool[] selected, int atom) =>
        selected == null || (atom >= 0 && atom < selected.Length && selected[atom]);

    // Mute a color toward a darker gray; 0 keeps it, 1 is fully muted.
    // Two effects grow with 'amount': the color fades to its own gray value, and its brightness
    // drops, to 40 % at amount 1. Alpha is kept.
    public static Color Dim(Color color, float amount)
    {
        float gray = color.grayscale;
        Color muted = Color.Lerp(color, new Color(gray, gray, gray), amount) * (1f - 0.6f * amount);
        muted.a = color.a;
        return muted;
    }

    // Find the client (this object first, then the scene) and the molecule when not assigned.
    private void Awake()
    {
        if (client == null) client = GetComponent<VMDClient>();
        if (client == null) client = FindAnyObjectByType<VMDClient>();
        if (molecule == null) molecule = FindAnyObjectByType<MoleculeRenderer>();
    }

    private void OnEnable() => lifetime = new CancellationTokenSource();

    // Cancel a pending selection request and remove the highlight.
    private void OnDisable()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        Clear();
    }

    // Highlight the next entry of 'selections'; after the last one, turn the highlight off.
    public void Cycle()
    {
        int next = cycleIndex + 1 < selections.Length ? cycleIndex + 1 : -1;
        if (next < 0)
        {
            Clear();
            return;
        }
        cycleIndex = next;
        Highlight(selections[next]);
    }

    // Restore VMD's colors and restart Cycle from the first selection. Also discards the reply
    // to a selection request that is still pending.
    public void Clear()
    {
        request++;
        cycleIndex = -1;
        ActiveStructure = -1;
        bool highlighted = mask != null;
        mask = null;
        summary = Active = Status = "";
        // Renderers only need restoring if they were recolored; QuickSurf would rebuild otherwise.
        if (highlighted)
            Apply();
    }

    // Highlight a bridge selection name or VMD selection text; a newer call or Clear wins.
    // Asks the bridge for the atom indices, builds the mask and applies it to the renderers.
    // The result is reported through Status: the number of selected atoms on success, the reason
    // on failure. A failed request leaves the previous highlight in place.
    public async void Highlight(string selection)
    {
        int version = ++request;
        if (client == null || !client.IsConnected || lifetime == null)
        {
            Status = "Highlight needs a VMD connection";
            return;
        }
        Status = $"Selecting {selection}...";
        try
        {
            VMDSelectionReply reply = await client.RequestSelection(selection, lifetime.Token);
            // A newer Highlight or Clear has run while waiting.
            if (version != request)
                return;
            // The mask covers the whole molecule, so it is sized by atomCount, not by indices.
            var selected = new bool[reply.atomCount];
            foreach (int atom in reply.indices)
                if (atom >= 0 && atom < selected.Length) selected[atom] = true;
            mask = selected;
            ActiveStructure = -1;
            Active = string.IsNullOrEmpty(reply.label) ? selection.Trim() : reply.label;
            summary = reply.indices.Length > 0
                ? $"Highlight {Active}: {reply.indices.Length} atoms"
                : $"Highlight {Active}: no atoms selected";
            Apply();
            Debug.Log($"[VMDHighlightController] {Status}", this);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != request)
                return;
            Status = "Highlight failed: " + error.Message;
            Debug.LogWarning($"[VMDHighlightController] {Status}", this);
        }
    }

    // The atoms that belong to an entry of 'selections', for a molecule of 'atomCount' atoms; null
    // until the entries are resolved. Starts resolving them when needed, and again when the atom
    // count no longer matches (another molecule). Cheap enough to call every frame.
    public int[] PrepareStructures(int atomCount)
    {
        if (structureOfAtom != null && structureOfAtom.Length != atomCount)
            DropStructures();
        if (structureOfAtom == null && !resolving && !resolveFailed && client != null && client.IsConnected && lifetime != null)
            ResolveStructures();
        return structureAtoms;
    }

    // Entry of 'selections' that contains 'atom', -1 for none or before the entries are resolved.
    public int StructureOf(int atom) =>
        structureOfAtom != null && atom >= 0 && atom < structureOfAtom.Length ? structureOfAtom[atom] : -1;

    // Highlight entry 'index' of 'selections' from the resolved structures, without a bridge
    // request; an invalid index restores VMD's colors. Also discards a pending Highlight reply.
    public void ShowStructure(int index)
    {
        if (structureMasks == null || index < 0 || index >= structureMasks.Length)
        {
            Clear();
            return;
        }
        request++;
        cycleIndex = index;
        ActiveStructure = index;
        mask = structureMasks[index];
        Active = structureLabels[index];
        summary = structureSizes[index] > 0
            ? $"Highlight {Active}: {structureSizes[index]} atoms"
            : $"Highlight {Active}: no atoms selected";
        Apply();
    }

    // Ask the bridge for every entry of 'selections' in turn and build the structure tables. On
    // failure, Status says why and the entries are not requested again until the connection
    // drops, so a bridge without SELECT is not asked every frame.
    private async void ResolveStructures()
    {
        int version = ++resolveVersion;
        resolving = true;
        try
        {
            int count = selections.Length;
            var masks = new bool[count][];
            var labels = new string[count];
            var sizes = new int[count];
            int[] owner = null;
            for (int i = 0; i < count; i++)
            {
                VMDSelectionReply reply = await client.RequestSelection(selections[i], lifetime.Token);
                if (version != resolveVersion)
                    return;
                if (owner == null)
                {
                    owner = new int[reply.atomCount];
                    for (int atom = 0; atom < owner.Length; atom++) owner[atom] = -1;
                }
                else if (owner.Length != reply.atomCount)
                    throw new InvalidOperationException("VMD loaded another molecule while selecting");
                masks[i] = new bool[reply.atomCount];
                foreach (int atom in reply.indices)
                {
                    if (atom < 0 || atom >= owner.Length || masks[i][atom]) continue;
                    masks[i][atom] = true;
                    sizes[i]++;
                    if (owner[atom] < 0) owner[atom] = i;
                }
                labels[i] = string.IsNullOrEmpty(reply.label) ? selections[i].Trim() : reply.label;
            }
            if (owner == null)
                return;
            var members = new System.Collections.Generic.List<int>();
            for (int atom = 0; atom < owner.Length; atom++)
                if (owner[atom] >= 0) members.Add(atom);
            structureOfAtom = owner;
            structureAtoms = members.ToArray();
            structureMasks = masks;
            structureLabels = labels;
            structureSizes = sizes;
            Debug.Log($"[VMDHighlightController] Pointing highlight ready: {count} selections, {members.Count} atoms.", this);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != resolveVersion)
                return;
            resolveFailed = true;
            Status = "Pointing highlight unavailable: " + error.Message;
            Debug.LogWarning($"[VMDHighlightController] {Status}", this);
        }
        finally
        {
            if (version == resolveVersion)
                resolving = false;
        }
    }

    // Forget the resolved structures and any failure, and let a running resolve discard its result.
    private void DropStructures()
    {
        resolveVersion++;
        resolving = resolveFailed = false;
        structureOfAtom = structureAtoms = structureSizes = null;
        structureMasks = null;
        structureLabels = null;
    }

    // Re-apply once the renderers are rebuilt (VMD rep change, reload), and drop the highlight
    // and the resolved structures on disconnect, since VMD may serve another molecule after
    // reconnecting.
    private void LateUpdate()
    {
        bool connected = client != null && client.IsConnected;
        if (wasConnected && !connected)
        {
            DropStructures();
            if (mask != null || Status != "")
                Clear();
        }
        wasConnected = connected;
        if (mask == null || molecule == null)
            return;
        // A renderer was created, replaced or destroyed since the last Apply.
        if (molecule.GetComponentInChildren<VMDCoordinateScene>() != coordinateScene ||
            molecule.GetComponentInChildren<VMDCartoonScene>() != cartoonScene ||
            molecule.GetComponentInChildren<VMDQuickSurfScene>() != quickSurfScene)
            Apply();
    }

    // Hand the current mask (null to restore VMD's colors) to the live renderers that exist now,
    // remember them for LateUpdate, and update Status. The highlight is dropped first when a
    // renderer's atom count no longer matches the mask.
    private void Apply()
    {
        if (molecule == null)
            return;
        coordinateScene = molecule.GetComponentInChildren<VMDCoordinateScene>();
        cartoonScene = molecule.GetComponentInChildren<VMDCartoonScene>();
        quickSurfScene = molecule.GetComponentInChildren<VMDQuickSurfScene>();
        if (mask != null && (Mismatch(coordinateScene?.AtomCount) || Mismatch(cartoonScene?.AtomCount) ||
                             Mismatch(quickSurfScene?.AtomCount)))
        {
            // Another molecule is loaded now; its atom indices mean something else.
            mask = null;
            ActiveStructure = -1;
            summary = Active = "";
            DropStructures();
            Status = "Highlight cleared: VMD loaded another molecule";
        }
        if (coordinateScene != null) coordinateScene.SetHighlight(mask, dimming);
        if (cartoonScene != null) cartoonScene.SetHighlight(mask, dimming);
        if (quickSurfScene != null) quickSurfScene.SetHighlight(mask, dimming);
        // With no live renderer (VMD mesh route or atom view) nothing is recolored; say so.
        if (mask != null)
            Status = coordinateScene != null || cartoonScene != null || quickSurfScene != null
                ? summary
                : summary + " (shown for VDW, CPK, NewCartoon and QuickSurf only)";
    }

    // True when a renderer exists, knows its atom count, and that count differs from the mask's.
    private bool Mismatch(int? atomCount) => atomCount.HasValue && atomCount.Value > 0 && atomCount.Value != mask.Length;
}
