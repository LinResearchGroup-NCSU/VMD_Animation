// Run in an isolated Unity Editor project with the current runtime scripts.
// Copy this file into Assets/Editor; execute VMDHandshakeChecks.Run in batch mode.
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class VMDHandshakeChecks
{
    private static readonly MethodInfo ReadHandshake = typeof(VMDClient).GetMethod(
        "TryReadHandshake", BindingFlags.Instance | BindingFlags.NonPublic);
    private static int passed;

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("Failed: " + name);
        passed++;
        Debug.Log("PASS " + name);
    }

    private static void CheckReply(string reply, bool accepted, string error, bool handled = true)
    {
        var root = new GameObject("Handshake check");
        try
        {
            var client = root.AddComponent<VMDClient>();
            using (var bytes = new MemoryStream())
            using (var writer = new StreamWriter(bytes, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                object[] arguments = { reply, writer, false };
                Assert((bool)ReadHandshake.Invoke(client, arguments) == handled, "handled: " + reply);
                Assert((bool)arguments[2] == accepted && client.IsConnected == accepted, "connection gate: " + reply);
                Assert(error == null ? client.ConnectionError == null :
                    client.ConnectionError != null && client.ConnectionError.Contains(error), "error: " + reply);
                Assert(client.SendCommand("PING") == accepted, "command gate: " + reply);
                Assert(Encoding.UTF8.GetString(bytes.ToArray()) == (accepted ? "PING\n" : ""), "wire output: " + reply);
            }
        }
        finally { Object.DestroyImmediate(root); }
    }

    public static void Run()
    {
        passed = 0;
        int exit = 1;
        try
        {
            CheckReply("HELLO OK " + VMDClient.ProtocolVersion, true, null);
            int differentVersion = VMDClient.ProtocolVersion == 1 ? 2 : 1;
            CheckReply("HELLO OK " + differentVersion, false, "does not match");
            foreach (string reply in new[] {
                "HELLO OK garbage", "HELLO OK ", "HELLO OK", "HELLO OK 1 extra",
                "HELLO OK -1", "HELLO OK +1", "HELLO OK 2147483648", "HELLO OK 1.0",
                "HELLO OK 1 ", "HELLO OK  1", "HELLO OKAY 1"
            }) CheckReply(reply, false, "Invalid VMD bridge handshake reply");
            CheckReply("HELLO MISMATCH " + differentVersion, false, "does not match");
            CheckReply("ERROR {Unknown command 'HELLO'}", false, "does not match");
            CheckReply("READY VMD_VR_BRIDGE " + VMDClient.ProtocolVersion, false, null, false);
            Debug.Log("HANDSHAKE CHECKS: " + passed + " passed, 0 failed");
            exit = 0;
        }
        catch (Exception error) { Debug.LogException(error); }
        finally { EditorApplication.Exit(exit); }
    }
}
