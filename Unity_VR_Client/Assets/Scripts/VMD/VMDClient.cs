using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
// The only component that talks to VMD. It connects to the Tcl bridge (VMD_VR_Bridge/
// vmd_vr_bridge.tcl) on 127.0.0.1 over TCP; both directions are UTF-8 text, one line per message.
//
// Connection:
// - The bridge may be started (source start_vr.tcl in VMD) before or during Play mode: Update
//   retries every 2 s until it connects, and again whenever the bridge is reloaded or stopped.
// - After connecting it sends HELLO <ProtocolVersion>. IsConnected becomes true only when the
//   bridge answers HELLO OK with the same version, so no other command is sent before that.
//   A different version, or a bridge too old to know HELLO, stops the retries (retryBlocked).
//
// Sending: SendCommand writes one line. RequestTrajectory and RequestSelection also add a
// request ID and wait for the reply that carries it.
//
// Receiving: the read loop in ConnectToVMD hands every line to exactly one consumer:
// - SELJSON, and an "Unknown command 'SELECT'" error: the pending RequestSelection calls.
// - TRAJJSON, and errors that mention TRAJ_: the pending RequestTrajectory calls.
// - SCENEJSON / SCENESTATE / SCENEERROR: StaticSceneResponseReceived (RepresentationController).
// - COORDS: parsed into Coordinates, then CoordinatesReceived (MoleculeRenderer,
//   TrajectoryController).
// - OK FRAME <n>, the reply to FRAME and STEP: stored in VmdFrame, then GET_COORDS is sent.
// - STATE: VmdFrame and VmdFrameCount. Anything else is only logged.
// The loop is an async method started from Update and never uses ConfigureAwait(false), so under
// Unity's synchronization context it resumes on the main thread and raises the events there.
//
// Users: MoleculeRenderer (FRAME 0 on each connection, coordinates), RepresentationController
// (scene export commands), TrajectoryController (TRAJ_* requests, STEP, FRAME, status text) and
// VMDHighlightController (SELECT).
public class VMDClient : MonoBehaviour
{
    // Must equal protocol_version in vmd_vr_bridge.tcl; HELLO checks it after connecting.
    public const int ProtocolVersion = 1;
    // How long to wait for the reply to HELLO before dropping the connection.
    private const float HandshakeTimeoutSeconds = 5f;
    // Pause between connection attempts after a failed or dropped connection.
    private const float RetryIntervalSeconds = 2f;

    // One "{x y z}" group of a COORDS reply; the three captures are the numbers.
    private static readonly Regex CoordinatePattern = new Regex(
        @"\{\s*([^\s{}]+)\s+([^\s{}]+)\s+([^\s{}]+)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Default bridge port. Overridden by the VMD_VR_PORT environment variable,
    // and that by the "-vmdPort <port>" command-line argument of a built player.
    [SerializeField, Range(1, 65535)] private int port = 45454;

    // The connection being attempted or in use; null between attempts. The read loop compares it
    // with its own connection to notice that Disconnect has run in the meantime.
    private TcpClient client;
    // Outgoing stream of the connection; null until the bridge has accepted HELLO.
    private StreamWriter writer;
    // Atom positions from the last COORDS reply, in VMD coordinates, one per atom of VMD's top
    // molecule. Cleared on disconnect.
    private readonly List<Vector3> coordinates = new List<Vector3>();
    // Earliest Time.unscaledTime for the next connection attempt.
    private float nextConnectAttempt;
    // A protocol mismatch would only repeat on every retry; it waits for Play mode to restart.
    private bool retryBlocked;
    // Log a failure once per outage, not on every retry.
    private bool failureReported;

    // Set only after the bridge accepts HELLO, so no other command reaches a mismatched bridge.
    public bool IsConnected => writer != null;
    // The port in use, chosen once in Start (see ResolvePort).
    public int Port { get; private set; }
    // Why the last connection attempt failed or ended (null until a failure, and again once
    // connected). TrajectoryController shows it on the status bar.
    public string ConnectionError { get; private set; }
    // VMD's displayed frame from STATE broadcasts and FRAME/STEP replies (-1 until known).
    public int VmdFrame { get; private set; } = -1;
    // Number of frames of VMD's top molecule, from STATE broadcasts (-1 until known).
    public int VmdFrameCount { get; private set; } = -1;
    // Read-only view of the last coordinate frame; empty until the first COORDS reply and after a
    // disconnect. The same list is refilled by every reply.
    public IReadOnlyList<Vector3> Coordinates => coordinates;
    // Raised after each COORDS reply, with the Coordinates list.
    public event Action<IReadOnlyList<Vector3>> CoordinatesReceived;
    // Raised with each complete SCENEJSON, SCENESTATE or SCENEERROR line.
    public event Action<string> StaticSceneResponseReceived;
    // Raised with TRAJJSON / SELJSON lines and the bridge errors that belong to them. Only the
    // local Receive handlers of RequestTrajectory and RequestSelection subscribe.
    private event Action<string> TrajectoryResponseReceived;
    private event Action<string> SelectionResponseReceived;

    // Send one TRAJ_* command and wait for its reply. 'command' is the command without a request
    // ID, e.g. "TRAJ_READ <token> <start> <count>"; a new ID is inserted after the command word and
    // only the TRAJJSON reply carrying that ID completes the call, so late replies to earlier
    // requests are ignored. Called by TrajectoryController only.
    // Throws when the token is cancelled, VMD is or becomes disconnected, no reply arrives within
    // 15 s, the reply contains "error", or the bridge does not know the command.
    public async Task<VMDTrajectoryReply> RequestTrajectory(string command, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Receive(string line)
        {
            // Every TRAJJSON envelope starts with requestId; avoid parsing other requests' large
            // payloads.
            if (line.StartsWith("TRAJJSON {\"requestId\":\"" + id + "\"", StringComparison.Ordinal))
                completion.TrySetResult(line.Substring(9));
            // A bridge older than the TRAJ_ commands answers "Unknown command", with no request ID.
            else if (line.StartsWith("ERROR ", StringComparison.Ordinal) && line.Contains("TRAJ_"))
                completion.TrySetException(new InvalidOperationException("Reload the updated VMD bridge."));
        }
        TrajectoryResponseReceived += Receive;
        try
        {
            token.ThrowIfCancellationRequested();
            int space = command.IndexOf(' ');
            string wire = space < 0 ? command + " " + id : command.Substring(0,space) + " " + id + command.Substring(space);
            if (!SendCommand(wire)) throw new IOException("VMD is not connected.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Wake up every 50 ms to notice a cancellation, a disconnect or the timeout.
            while (!completion.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!IsConnected) throw new IOException("VMD disconnected.");
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("VMD coordinate request timed out.");
                await Task.WhenAny(completion.Task, Task.Delay(50, token));
            }
            string json = await completion.Task;
            // Parsed on a worker thread: a TRAJ_READ reply holds whole coordinate frames.
            var reply = await Task.Run(() => JsonUtility.FromJson<VMDTrajectoryReply>(json), token);
            token.ThrowIfCancellationRequested();
            if (reply == null) throw new FormatException("Empty trajectory reply.");
            if (!string.IsNullOrEmpty(reply.error)) throw new InvalidOperationException(reply.error);
            return reply;
        }
        finally
        {
            TrajectoryResponseReceived -= Receive;
            // If the wait ended for another reason, read a stored error so it counts as observed.
            if (completion.Task.IsFaulted) _ = completion.Task.Exception;
        }
    }

    // Resolve a VMD selection (a bridge name such as C_DOMAIN, or VMD selection text) to atom
    // indices without changing VMD. Replies use their own SELJSON envelope, separate from
    // trajectory requests, so a failed or unsupported SELECT never affects playback.
    // Called by VMDHighlightController. Waits and throws like RequestTrajectory; the returned
    // reply has a non-null 'indices' array.
    public async Task<VMDSelectionReply> RequestSelection(string selection, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(selection))
            throw new ArgumentException("Empty selection.");
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Receive(string line)
        {
            if (line.StartsWith("SELJSON {\"requestId\":\"" + id + "\"", StringComparison.Ordinal))
                completion.TrySetResult(line.Substring(8));
            // The read loop forwards only the "Unknown command 'SELECT'" error to this event.
            else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                completion.TrySetException(new NotSupportedException("This VMD bridge has no SELECT; source the updated start_vr.tcl."));
        }
        SelectionResponseReceived += Receive;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!SendCommand("SELECT " + id + " " + selection.Trim())) throw new IOException("VMD is not connected.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!completion.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!IsConnected) throw new IOException("VMD disconnected.");
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("VMD selection request timed out.");
                await Task.WhenAny(completion.Task, Task.Delay(50, token));
            }
            string json = await completion.Task;
            var reply = JsonUtility.FromJson<VMDSelectionReply>(json);
            if (reply == null) throw new FormatException("Empty selection reply.");
            if (!string.IsNullOrEmpty(reply.error)) throw new InvalidOperationException(reply.error);
            if (reply.atomCount < 1) throw new FormatException("Invalid selection reply.");
            reply.indices ??= Array.Empty<int>();
            return reply;
        }
        finally
        {
            SelectionResponseReceived -= Receive;
            // As in RequestTrajectory: mark a stored error as observed.
            if (completion.Task.IsFaulted) _ = completion.Task.Exception;
        }
    }

    // Resolve the port once; Update connects, and disable or application quit disconnects.
    private void Start()
    {
        Port = ResolvePort();
    }

    // Connect as soon as possible, then retry every RetryIntervalSeconds after a failed or dropped
    // connection. 'client' stays non-null for as long as an attempt or a connection is alive.
    private void Update()
    {
        if (client == null && !retryBlocked && Time.unscaledTime >= nextConnectAttempt)
            ConnectToVMD();
    }

    // Command line first, then environment variable, then the Inspector value.
    private int ResolvePort()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], "-vmdPort", StringComparison.OrdinalIgnoreCase) &&
                TryParsePort(arguments[i + 1], "-vmdPort", out int fromArgument))
                return fromArgument;
        }

        string fromEnvironment = Environment.GetEnvironmentVariable("VMD_VR_PORT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) &&
            TryParsePort(fromEnvironment.Trim(), "VMD_VR_PORT", out int environmentPort))
            return environmentPort;

        return port;
    }

    // Parse a port number from 'source' (named in the warning). An invalid value is ignored with a
    // warning, and the next source in ResolvePort is used.
    private bool TryParsePort(string value, string source, out int result)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) &&
            result >= 1 && result <= 65535)
            return true;
        Debug.LogWarning($"[VMDClient] Ignoring {source}='{value}': expected a port between 1 and 65535.", this);
        return false;
    }

    // One connection attempt and, when it succeeds, the whole life of that connection: connect,
    // send HELLO, then read and dispatch lines until the connection ends (see the header for where
    // each kind of line goes). Started by Update; it returns only when the attempt has failed or
    // the connection is over, after which Update starts the next attempt.
    // Every "client != connection" test means that Disconnect has run since this attempt began.
    private async void ConnectToVMD()
    {
        TcpClient connection = new TcpClient();
        client = connection;

        try
        {
            if (!failureReported)
                Debug.Log($"[VMDClient] Connecting to 127.0.0.1:{Port}...", this);
            await connection.ConnectAsync("127.0.0.1", Port);

            // Ignore a connection completed after this component was disabled.
            if (client != connection)
                return;

            connection.NoDelay = true;
            connection.SendTimeout = 1000;

            using (NetworkStream stream = connection.GetStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            using (StreamWriter output = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                // The Tcl bridge expects UTF-8, one command per LF line.
                output.NewLine = "\n";
                output.AutoFlush = true;
                // writer stays null (not connected) until the bridge accepts this protocol version.
                output.WriteLine("HELLO " + ProtocolVersion.ToString(CultureInfo.InvariantCulture));
                _ = ExpireHandshake(connection);

                while (client == connection)
                {
                    string response = await reader.ReadLineAsync();
                    if (client != connection)
                        break;

                    // End of stream: the bridge closed the connection.
                    if (response == null)
                    {
                        ConnectionError = writer == null
                            ? "VMD closed the connection before the handshake; reconnecting."
                            : "VMD closed the connection; reconnecting.";
                        ReportFailure(ConnectionError);
                        break;
                    }

                    // Until the handshake is done, look for the reply to HELLO. The bridge's
                    // greeting (READY, STATE) arrives first and falls through to the dispatch below.
                    if (writer == null && TryReadHandshake(response, output, out bool accepted))
                    {
                        // Wrong or unreadable protocol version: stop, and do not retry.
                        if (!accepted)
                        {
                            retryBlocked = true;
                            ConnectionError += " Then restart Play mode.";
                            Debug.LogError($"[VMDClient] {ConnectionError}", this);
                            break;
                        }
                        continue;
                    }

                    // A bridge older than SELECT answers "Unknown command 'SELECT'". That error goes
                    // to the selection requests, which then fail at once instead of timing out.
                    if (response.StartsWith("SELJSON ", StringComparison.Ordinal) ||
                        (response.StartsWith("ERROR ", StringComparison.Ordinal) && response.Contains("'SELECT'")))
                    {
                        SelectionResponseReceived?.Invoke(response);
                        continue;
                    }

                    if (response.StartsWith("TRAJJSON ", StringComparison.Ordinal) ||
                        (response.StartsWith("ERROR ", StringComparison.Ordinal) && response.Contains("TRAJ_")))
                    {
                        TrajectoryResponseReceived?.Invoke(response);
                        continue;
                    }

                    if (response.StartsWith("SCENEJSON ", StringComparison.Ordinal) ||
                        response.StartsWith("SCENESTATE ", StringComparison.Ordinal) ||
                        response.StartsWith("SCENEERROR ", StringComparison.Ordinal))
                    {
                        StaticSceneResponseReceived?.Invoke(response);
                        continue;
                    }

                    if (TryReadCoordinates(response))
                    {
                        Debug.Log($"[VMDClient] Read {coordinates.Count} atom coordinates.", this);
                        CoordinatesReceived?.Invoke(coordinates);
                    }
                    else
                    {
                        Debug.Log($"[VMDClient] Received: {response}", this);

                        // FRAME and STEP both acknowledge the applied frame with OK FRAME.
                        // Request its coordinates only after VMD has completed the change.
                        if (response.StartsWith("OK FRAME ", StringComparison.Ordinal))
                        {
                            if (int.TryParse(response.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
                                VmdFrame = frame;
                            RequestCoordinates();
                        }
                        else if (response.StartsWith("STATE ", StringComparison.Ordinal))
                            ReadState(response);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Closing the socket during normal cleanup can interrupt an await; that case
            // (client != connection) is not an error and is not reported.
            if (client == connection)
            {
                ConnectionError = $"Cannot reach the VMD bridge on port {Port}; retrying every {RetryIntervalSeconds:0} s.";
                ReportFailure($"Connection error: {exception.Message} Run source start_vr.tcl in VMD; Unity keeps retrying.");
            }
        }
        finally
        {
            if (client == connection)
                Disconnect();
            connection.Close();
        }
    }

    // Handles the bridge's reply to HELLO; returns false for any other line.
    // 'accepted' is true only for "HELLO OK <ProtocolVersion>"; 'output' then becomes the writer,
    // which is what makes IsConnected true. For every other reply ConnectionError says why:
    // a malformed "HELLO OK" line, "HELLO OK" with another version, or "HELLO MISMATCH <version>".
    // A bridge older than the handshake answers HELLO with "Unknown command".
    private bool TryReadHandshake(string response, StreamWriter output, out bool accepted)
    {
        accepted = false;
        string bridgeVersion;
        if (response.StartsWith("HELLO OK", StringComparison.Ordinal))
        {
            string[] words = response.Split(' ');
            if (words.Length != 3 || words[1] != "OK" ||
                !int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out int replyVersion))
            {
                ConnectionError = "Invalid VMD bridge handshake reply. " +
                                  "Source the VMD scripts that came with this build.";
                return true;
            }

            if (replyVersion == ProtocolVersion)
            {
                accepted = true;
                writer = output;
                ConnectionError = null;
                failureReported = false;
                Debug.Log($"[VMDClient] Connected (protocol {ProtocolVersion}).", this);
                return true;
            }
            bridgeVersion = words[2];
        }
        else if (response.StartsWith("HELLO MISMATCH ", StringComparison.Ordinal))
            bridgeVersion = response.Substring("HELLO MISMATCH ".Length);
        else if (response.StartsWith("ERROR ", StringComparison.Ordinal) && response.Contains("'HELLO'"))
            bridgeVersion = "0 (no handshake)";
        else
            return false;

        ConnectionError = $"VMD bridge protocol {bridgeVersion} does not match client protocol {ProtocolVersion}. " +
                          "Source the VMD scripts that came with this build.";
        return true;
    }

    // Drop a connection whose peer never answers HELLO, e.g. another program on the port.
    private async Task ExpireHandshake(TcpClient connection)
    {
        await Task.Delay(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
        if (client != connection || writer != null)
            return;
        ConnectionError = $"No handshake reply on port {Port}; is the VMD bridge listening there?";
        ReportFailure(ConnectionError);
        Disconnect();
    }

    // Warn once per outage; retries stay silent until a connection succeeds again.
    private void ReportFailure(string message)
    {
        if (failureReported)
            return;
        failureReported = true;
        Debug.LogWarning($"[VMDClient] {message}", this);
    }

    // Send one command line to the bridge. Call on Unity's main thread.
    // True means sent to TCP; the VMD response confirms whether it was executed. False, with a
    // warning, when the command is empty or contains a line break, or when VMD is not connected.
    // A failed write also drops the connection, and Update then reconnects.
    public bool SendCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains("\n") || command.Contains("\r"))
        {
            Debug.LogWarning("[VMDClient] A command must be a single non-empty line.", this);
            return false;
        }

        if (!IsConnected)
        {
            Debug.LogWarning($"[VMDClient] Not connected; command not sent: {command}", this);
            return false;
        }

        try
        {
            writer.WriteLine(command);
            Debug.Log($"[VMDClient] Sent: {command}", this);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[VMDClient] Send failed: {exception.Message}", this);
            Disconnect();
            return false;
        }
    }

    // "STATE molid frame frameCount {matrices...}" from the bridge's poll and connection greeting.
    // Only the frame and the frame count are used; the matrices are ignored.
    private void ReadState(string response)
    {
        string[] words = response.Split(new[] { ' ' }, 5);
        if (words.Length >= 4 &&
            int.TryParse(words[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame) &&
            int.TryParse(words[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            VmdFrame = frame;
            VmdFrameCount = count;
        }
    }

    // Request the coordinates of VMD's displayed frame. The COORDS reply arrives through the read
    // loop and raises CoordinatesReceived. Sent by the read loop after every OK FRAME reply.
    public void RequestCoordinates()
    {
        SendCommand("GET_COORDS");
    }

    // Parse a VMD response line for coordinates, e.g. "COORDS {x1 y1 z1} {x2 y2 z2} ...".
    // Returns true if coordinates were successfully parsed and stored in the coordinates list.
    // "COORDS {}" is a valid empty frame. A malformed line leaves the previous coordinates
    // unchanged and returns false.
    private bool TryReadCoordinates(string response)
    {
        if (!response.StartsWith("COORDS ", StringComparison.Ordinal))
            return false;

        MatchCollection matches = CoordinatePattern.Matches(response, "COORDS ".Length);
        if (matches.Count == 0 && !string.Equals(response, "COORDS {}", StringComparison.Ordinal))
        {
            Debug.LogWarning($"[VMDClient] Invalid coordinate response: {response}", this);
            return false;
        }

        List<Vector3> parsedCoordinates = new List<Vector3>(matches.Count);

        foreach (Match match in matches)
        {
            if (!float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                Debug.LogWarning($"[VMDClient] Invalid coordinate response: {response}", this);
                return false;
            }

            parsedCoordinates.Add(new Vector3(x, y, z));
        }

        coordinates.Clear();
        coordinates.AddRange(parsedCoordinates);
        return true;
    }

    // Clear the connection state, schedule the next attempt, and close the TCP socket, which also
    // ends the read loop in ConnectToVMD. Safe to call more than once.
    private void Disconnect()
    {
        TcpClient connection = client;
        bool wasConnected = writer != null;
        client = null;
        writer = null;
        VmdFrame = VmdFrameCount = -1;
        // A reconnect starts like the first connection: consumers wait for fresh coordinates,
        // since the bridge may now serve another molecule.
        coordinates.Clear();
        nextConnectAttempt = Time.unscaledTime + RetryIntervalSeconds;
        if (connection == null)
            return;

        // Closing the socket releases any pending read; its using blocks clean up.
        connection.Close();
        if (wasConnected)
            Debug.Log("[VMDClient] Disconnected.", this);
    }

    // Re-enabling the component connects again, even after a protocol mismatch.
    private void OnEnable()
    {
        retryBlocked = false;
        failureReported = false;
        nextConnectAttempt = 0f;
    }

    // Close the connection when the component is disabled.
    private void OnDisable()
    {
        Disconnect();
    }

    // Close the connection when the application exits.
    private void OnApplicationQuit()
    {
        Disconnect();
    }
}
