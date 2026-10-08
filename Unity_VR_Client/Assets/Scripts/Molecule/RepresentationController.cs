using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Phase 1: request a VMD scene export and display its OBJ geometry.
// Keyboard/XR components call these public methods; no input bindings live here.
[DisallowMultipleComponent]
public class RepresentationController : MonoBehaviour
{
    [SerializeField] private bool exportOnConnect = true;
    [SerializeField, Min(0)] private int representationIndex;
    [SerializeField] private string localObjPath = "";
    [SerializeField] private string currentDrawingMethod = "";
    private VMDClient client;
    private MoleculeRenderer molecule;
    private GameObject preview;
    private CancellationTokenSource loading;
    private string requestId;
    private float sentAt;
    private int revision;
    private bool initialRequested, isLoading;
    private static readonly string[] SupportedMethods = { "VDW", "CPK", "QuickSurf", "NewCartoon" };

    [Serializable] private class ExportReply
    {
        public string requestId = "", path = "", message = "";
        public int molecule = -1, frame = -1;
        public RepInfo[] reps = Array.Empty<RepInfo>();
    }
    [Serializable] private class RepInfo { public int id = -1, visible = 0; public string method = ""; }

    public bool HasStaticScene => preview != null;
    public bool IsBusy => requestId != null || isLoading;
    public string CurrentDrawingMethod => currentDrawingMethod;

    public void Initialize(VMDClient source, MoleculeRenderer owner)
    {
        if (client != null) client.StaticSceneResponseReceived -= Receive;
        client = source;
        molecule = owner;
        if (client != null && isActiveAndEnabled) client.StaticSceneResponseReceived += Receive;
    }

    private void OnEnable()
    {
        if (client != null)
        {
            client.StaticSceneResponseReceived -= Receive;
            client.StaticSceneResponseReceived += Receive;
        }
        initialRequested = false;
    }

    private void Update()
    {
        if (client == null || molecule == null) return;
        if (!client.IsConnected)
        {
            initialRequested = false;
            if (IsBusy) CancelOperation();
            return;
        }
        if (requestId != null && Time.unscaledTime - sentAt > 120f)
        {
            CancelOperation();
            Debug.LogWarning("[RepresentationController] Export timed out. Reload the updated VMD bridge.", this);
        }
        // Wait for startup coordinates so they cannot overwrite the first static preview.
        if (exportOnConnect && !initialRequested && client.Coordinates.Count > 0)
        {
            initialRequested = true;
            RequestStaticExport();
        }
    }

    [ContextMenu("Export current VMD scene")]
    public void RefreshFromVMD() => RequestStaticExport();

    public bool RequestStaticExport()
    {
        return SendExportCommand("EXPORT_SCENE");
    }

    // These wrappers can be wired directly to UnityEvents / future XR buttons.
    public void SetVDW() => SetRepresentation("VDW");
    public void SetCPK() => SetRepresentation("CPK");
    public void SetQuickSurf() => SetRepresentation("QuickSurf");
    public void SetNewCartoon() => SetRepresentation("NewCartoon");

    public void SetRepresentation(string method) => SetRepresentation(representationIndex, method);

    public bool SetRepresentation(int repIndex, string method)
    {
        string canonical = Array.Find(SupportedMethods,
            value => string.Equals(value, method, StringComparison.OrdinalIgnoreCase));
        if (repIndex < 0 || canonical == null)
        {
            Debug.LogWarning("[RepresentationController] Choose VDW, CPK, QuickSurf or NewCartoon and a valid rep index.", this);
            return false;
        }
        bool sent = SendExportCommand("SET_REPRESENTATION", repIndex + " " + canonical);
        if (sent) representationIndex = repIndex;
        return sent;
    }

    public void NextRepresentation() => Cycle(1);
    public void PreviousRepresentation() => Cycle(-1);

    private void Cycle(int direction)
    {
        int current = Array.IndexOf(SupportedMethods, currentDrawingMethod);
        int next = current < 0 ? 0 : (current + direction + SupportedMethods.Length) % SupportedMethods.Length;
        SetRepresentation(SupportedMethods[next]);
    }

    private bool SendExportCommand(string command, string arguments = null)
    {
        if (!isActiveAndEnabled || client == null || !client.IsConnected || IsBusy)
        {
            Debug.LogWarning("[RepresentationController] Connect to VMD and wait for the current import before exporting.", this);
            return false;
        }
        requestId = Guid.NewGuid().ToString("N");
        sentAt = Time.unscaledTime;
        string line = command + " " + requestId + (arguments == null ? "" : " " + arguments);
        if (client.SendCommand(line))
        {
            initialRequested = true;
            return true;
        }
        requestId = null;
        return false;
    }

    private void Receive(string response)
    {
        try
        {
            bool failed = response.StartsWith("SCENEERROR ", StringComparison.Ordinal);
            var reply = JsonUtility.FromJson<ExportReply>(response.Substring(failed ? 11 : 10));
            if (reply == null || requestId == null || reply.requestId != requestId) return;
            requestId = null;
            if (failed)
            {
                Debug.LogWarning("[RepresentationController] VMD: " + reply.message, this);
                return;
            }
            if (string.IsNullOrWhiteSpace(reply.path)) throw new FormatException("Export response has no OBJ path.");
            string method = "";
            if (reply.reps != null)
                foreach (var rep in reply.reps)
                    if (rep.id == representationIndex) method = rep.method;
            LoadScene(reply.path, method);
        }
        catch (Exception error)
        {
            requestId = null;
            Debug.LogWarning("[RepresentationController] Invalid export response: " + error.Message, this);
        }
    }

    [ContextMenu("Load local VMD OBJ")]
    public void LoadLocalFile() => LoadStaticScene(localObjPath);

    public void LoadStaticScene(string path)
    {
        if (!isActiveAndEnabled || molecule == null) return;
        CancelOperation();
        initialRequested = true;
        LoadScene(path, "");
    }

    private async void LoadScene(string path, string method)
    {
        int version = ++revision;
        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        isLoading = true;
        GameObject staging = null;
        try
        {
            var data = await Task.Run(() => VMDObjLoader.Load(path, cancellation.Token), cancellation.Token);
            if (version != revision || !isActiveAndEnabled || molecule == null) return;
            staging = new GameObject("VMD static scene");
            staging.SetActive(false);
            staging.transform.SetParent(molecule.transform, false);
            staging.AddComponent<VMDStaticScene>().Load(data, molecule.CoordinateScale);
            ClearPreview();
            preview = staging;
            staging = null;
            molecule.HideLegacyAtoms();
            preview.SetActive(true);
            currentDrawingMethod = method;
            foreach (string warning in data.warnings) Debug.LogWarning("[VMD OBJ] " + warning, this);
            Debug.Log($"[VMD OBJ] Loaded {data.triangleCount} triangles from {path}", this);
            if (data.triangleCount > 0) FitView();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version == revision)
                Debug.LogError("[RepresentationController] Static import failed: " + error.Message, this);
        }
        finally
        {
            if (staging != null) Destroy(staging);
            if (version == revision) { loading = null; isLoading = false; }
            cancellation.Dispose();
        }
    }

    // Called by MoleculeRenderer when real coordinates change. Phase 1 is not a
    // mesh stream: resume the original atom rendering instead of showing a stale frame.
    public bool OnCoordinatesUpdated()
    {
        bool refit = HasStaticScene;
        if (refit || IsBusy)
        {
            CancelOperation();
            ClearPreview();
        }
        return refit;
    }

    [ContextMenu("Return to coordinate view")]
    public void ShowAtomView()
    {
        CancelOperation();
        ClearPreview();
        initialRequested = true;
        if (molecule != null && client != null && molecule.isActiveAndEnabled)
        {
            molecule.RenderMolecule(client.Coordinates);
            FitView();
        }
    }

    public void FitView()
    {
        var cameraController = FindAnyObjectByType<VMDCameraController>();
        if (cameraController != null) cameraController.FitMolecule();
    }

    private void CancelOperation()
    {
        revision++;
        loading?.Cancel();
        loading = null;
        isLoading = false;
        requestId = null;
    }

    private void ClearPreview()
    {
        if (preview != null) { preview.SetActive(false); Destroy(preview); }
        preview = null;
        currentDrawingMethod = "";
    }

    private void OnDisable()
    {
        if (client != null) client.StaticSceneResponseReceived -= Receive;
        ShowAtomView();
    }
}
