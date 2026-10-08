using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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
    public IReadOnlyList<Vector3> Coordinates => coordinates;
    // Notify subscribers when new coordinates are received from VMD.
    public event Action<IReadOnlyList<Vector3>> CoordinatesReceived;
    public event Action<string> StaticSceneResponseReceived;

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

                    if (response.StartsWith("SCENEJSON ", StringComparison.Ordinal) ||
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
                            RequestCoordinates();
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

    private void Disconnect()
    {
        TcpClient connection = client;
        client = null;
        writer = null;
        if (connection == null)
            return;

        // Closing the socket releases any pending read; its using blocks clean up.
        connection.Close();
        Debug.Log("[VMDClient] Disconnected.", this);
    }

    private void OnDisable()
    {
        Disconnect();
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }
}
