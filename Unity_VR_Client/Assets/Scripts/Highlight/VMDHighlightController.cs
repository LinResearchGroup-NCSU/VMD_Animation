using System;
using System.Threading;
using UnityEngine;

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
public class VMDHighlightController : MonoBehaviour
{
    [SerializeField] private VMDClient client;
    [SerializeField] private MoleculeRenderer molecule;
    [Tooltip("Bridge selection names (see ::vrbridge::define_selection) or VMD selection text, in Cycle order.")]
    [SerializeField] private string[] selections = { "N_DOMAIN", "C_DOMAIN", "LINKER" };
    [Tooltip("How strongly atoms outside the selection are muted: 0 unchanged, 1 dark gray.")]
    [SerializeField, Range(0f, 1f)] private float dimming = 0.75f;

    private bool[] mask;
    private string summary = "";
    private int cycleIndex = -1, request;
    private bool wasConnected;
    private CancellationTokenSource lifetime;
    private VMDCoordinateScene coordinateScene;
    private VMDCartoonScene cartoonScene;
    private VMDQuickSurfScene quickSurfScene;

    // The highlighted selection ("" when off) and one status line, e.g. for an XR panel.
    public string Active { get; private set; } = "";
    public string Status { get; private set; } = "";

    // A null selection means no highlight, so every atom keeps its color.
    public static bool IsSelected(bool[] selected, int atom) =>
        selected == null || (atom >= 0 && atom < selected.Length && selected[atom]);

    // Mute a color toward a darker gray; 0 keeps it, 1 is fully muted.
    public static Color Dim(Color color, float amount)
    {
        float gray = color.grayscale;
        Color muted = Color.Lerp(color, new Color(gray, gray, gray), amount) * (1f - 0.6f * amount);
        muted.a = color.a;
        return muted;
    }

    private void Awake()
    {
        if (client == null) client = GetComponent<VMDClient>();
        if (client == null) client = FindAnyObjectByType<VMDClient>();
        if (molecule == null) molecule = FindAnyObjectByType<MoleculeRenderer>();
    }

    private void OnEnable() => lifetime = new CancellationTokenSource();

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

    // Restore VMD's colors and restart Cycle from the first selection.
    public void Clear()
    {
        request++;
        cycleIndex = -1;
        bool highlighted = mask != null;
        mask = null;
        summary = Active = Status = "";
        // Renderers only need restoring if they were recolored; QuickSurf would rebuild otherwise.
        if (highlighted)
            Apply();
    }

    // Highlight a bridge selection name or VMD selection text; a newer call or Clear wins.
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
            if (version != request)
                return;
            var selected = new bool[reply.atomCount];
            foreach (int atom in reply.indices)
                if (atom >= 0 && atom < selected.Length) selected[atom] = true;
            mask = selected;
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

    // Re-apply once the renderers are rebuilt (VMD rep change, reload), and drop the highlight
    // on disconnect, since VMD may serve another molecule after reconnecting.
    private void LateUpdate()
    {
        bool connected = client != null && client.IsConnected;
        if (wasConnected && !connected && (mask != null || Status != ""))
            Clear();
        wasConnected = connected;
        if (mask == null || molecule == null)
            return;
        if (molecule.GetComponentInChildren<VMDCoordinateScene>() != coordinateScene ||
            molecule.GetComponentInChildren<VMDCartoonScene>() != cartoonScene ||
            molecule.GetComponentInChildren<VMDQuickSurfScene>() != quickSurfScene)
            Apply();
    }

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
            summary = Active = "";
            Status = "Highlight cleared: VMD loaded another molecule";
        }
        if (coordinateScene != null) coordinateScene.SetHighlight(mask, dimming);
        if (cartoonScene != null) cartoonScene.SetHighlight(mask, dimming);
        if (quickSurfScene != null) quickSurfScene.SetHighlight(mask, dimming);
        if (mask != null)
            Status = coordinateScene != null || cartoonScene != null || quickSurfScene != null
                ? summary
                : summary + " (shown for VDW, CPK, NewCartoon and QuickSurf only)";
    }

    private bool Mismatch(int? atomCount) => atomCount.HasValue && atomCount.Value > 0 && atomCount.Value != mask.Length;
}
