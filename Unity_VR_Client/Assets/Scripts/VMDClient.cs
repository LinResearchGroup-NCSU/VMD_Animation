using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class VMDClient : MonoBehaviour
{
    private TcpClient client;
    private StreamWriter writer;

    public bool IsConnected => writer != null;

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

                    Debug.Log($"[VMDClient] Received: {response}", this);
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
