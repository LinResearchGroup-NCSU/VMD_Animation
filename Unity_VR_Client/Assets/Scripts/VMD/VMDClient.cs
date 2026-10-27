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
// A Unity component that connects to a VMD Tcl bridge over TCP, sends commands, and receives atom coordinates.
// The VMD Tcl bridge must be started in VMD before Play mode, e.g. by running the Tcl script in the VMD console:
public class VMDClient : MonoBehaviour
{
    private static readonly Regex CoordinatePattern = new Regex(
        @"\{\s*([^\s{}]+)\s+([^\s{}]+)\s+([^\s{}]+)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private TcpClient client;
    private StreamWriter writer;
    private readonly List<Vector3> coordinates = new List<Vector3>();

    public bool IsConnected => writer != null;
    // VMD's displayed frame from STATE broadcasts and FRAME/STEP replies (-1 until known).
    public int VmdFrame { get; private set; } = -1;
    public int VmdFrameCount { get; private set; } = -1;
    public IReadOnlyList<Vector3> Coordinates => coordinates;
    // Notify subscribers when new coordinates are received from VMD.
    public event Action<IReadOnlyList<Vector3>> CoordinatesReceived;
    public event Action<string> StaticSceneResponseReceived;
    private event Action<string> TrajectoryResponseReceived;

    // Shared by coordinate playback and future XR controls. Request IDs isolate late replies.
    public async Task<VMDTrajectoryReply> RequestTrajectory(string command, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Receive(string line)
        {
            // Every TRAJJSON envelope starts with requestId; avoid parsing other requests' large payloads.
            if (line.StartsWith("TRAJJSON {\"requestId\":\"" + id + "\"", StringComparison.Ordinal))
                completion.TrySetResult(line.Substring(9));
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
            while (!completion.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!IsConnected) throw new IOException("VMD disconnected.");
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("VMD coordinate request timed out.");
                await Task.WhenAny(completion.Task, Task.Delay(50, token));
            }
            string json = await completion.Task;
            var reply = await Task.Run(() => JsonUtility.FromJson<VMDTrajectoryReply>(json), token);
            token.ThrowIfCancellationRequested();
            if (reply == null) throw new FormatException("Empty trajectory reply.");
            if (!string.IsNullOrEmpty(reply.error)) throw new InvalidOperationException(reply.error);
            return reply;
        }
        finally
        {
            TrajectoryResponseReceived -= Receive;
            if (completion.Task.IsFaulted) _ = completion.Task.Exception;
        }
    }

    // Connect to VMD bridge on start, and disconnect on disable or application quit.
    private void Start()
    {
        ConnectToVMD();
    }
    
    // Connect to VMD bridge
    private async void ConnectToVMD()
    {
        TcpClient connection = new TcpClient();
        client = connection;

        try
        {
            Debug.Log("[VMDClient] Connecting to 127.0.0.1:45454...", this);
            await connection.ConnectAsync("127.0.0.1", 45454);

            // Ignore a connection completed after this component was disabled.
            if (client != connection)
                return;

            connection.NoDelay = true;
            connection.SendTimeout = 1000;

            using (NetworkStream stream = connection.GetStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            using (StreamWriter output = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                // The existing Tcl bridge expects UTF-8, one command per LF line.
                output.NewLine = "\n";
                output.AutoFlush = true;
                writer = output;
                Debug.Log("[VMDClient] Connected.", this);

                while (client == connection)
                {
                    string response = await reader.ReadLineAsync();
                    if (client != connection)
                        break;

                    if (response == null)
                    {
                        Debug.LogWarning("[VMDClient] VMD closed the connection. Restart Play mode to reconnect.", this);
                        break;
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
            // Closing the socket during normal cleanup can interrupt an await.
            if (client == connection)
                Debug.LogError($"[VMDClient] Connection error: {exception.Message} Start the bridge, then restart Play mode.", this);
        }
        finally
        {
            if (client == connection)
                Disconnect();
            connection.Close();
        }
    }

    // Call on Unity's main thread from keyboard controls, UI, or future VR input.
    // True means sent to TCP; the VMD response confirms whether it was executed.
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

    // Request the coordinates of the current frame from VMD.
    public void RequestCoordinates()
    {
        SendCommand("GET_COORDS");
    }

    // Parse a VMD response line for coordinates, e.g. "COORDS {x1 y1 z1} {x2 y2 z2} ...".
    // Returns true if coordinates were successfully parsed and stored in the coordinates list.
    // Can be called from MoleculeRenderer.
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

    // Clear the connection state and close the TCP socket, which also ends the read loop
    // in ConnectToVMD. Safe to call more than once.
    private void Disconnect()
    {
        TcpClient connection = client;
        client = null;
        writer = null;
        VmdFrame = VmdFrameCount = -1;
        if (connection == null)
            return;

        // Closing the socket releases any pending read; its using blocks clean up.
        connection.Close();
        Debug.Log("[VMDClient] Disconnected.", this);
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
