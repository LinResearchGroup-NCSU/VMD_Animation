// Editor check of the client side of the HELLO protocol handshake (README, "Protocol
// handshake"): how VMDClient.TryReadHandshake treats each possible reply line. It needs no VMD,
// no TCP connection, no fixtures and no Play mode; the method is private and is called through
// reflection. The bridge's side of the handshake is covered by VMD_VR_Bridge/_test_bridge.tcl.
//
// For each reply line it checks:
// - whether the line is recognized as a handshake reply,
// - whether the client becomes connected (only "HELLO OK" with the client's own version),
// - the text of VMDClient.ConnectionError,
// - that SendCommand writes to the stream only after an accepted handshake.
// Reply lines: the matching version, another version, malformed "HELLO OK" lines, "HELLO
// MISMATCH", the "Unknown command 'HELLO'" error of a bridge without the handshake, and the
// bridge's READY greeting, which is not a handshake reply.
//
// This folder is outside Assets, so the main project does not compile this file.
// Run in an isolated Unity Editor project with the current runtime scripts.
// Copy this file into Assets/Editor; execute VMDHandshakeChecks.Run in batch mode
// (-batchmode -executeMethod VMDHandshakeChecks.Run).
// Each check logs "PASS <name>"; the Editor exits with code 0 when all passed, otherwise 1.
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class VMDHandshakeChecks
{
    // The private method under test: bool TryReadHandshake(string, StreamWriter, out bool).
    private static readonly MethodInfo ReadHandshake = typeof(VMDClient).GetMethod(
        "TryReadHandshake", BindingFlags.Instance | BindingFlags.NonPublic);
    // Number of checks passed so far, for the summary line.
    private static int passed;

    // Throw on a failed check, which ends the run; count and log "PASS" otherwise.
    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("Failed: " + name);
        passed++;
        Debug.Log("PASS " + name);
    }

    // Feed one reply line to a new VMDClient and run the checks listed in the header.
    // - accepted: the handshake is expected to succeed.
    // - error: text that ConnectionError must contain, or null when it must stay null.
    // - handled: the line is expected to be recognized as a handshake reply.
    private static void CheckReply(string reply, bool accepted, string error, bool handled = true)
    {
        var root = new GameObject("Handshake check");
        try
        {
            var client = root.AddComponent<VMDClient>();
            using (var bytes = new MemoryStream())
            using (var writer = new StreamWriter(bytes, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                // The third element receives the method's 'out bool accepted'.
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

    // Entry point for -executeMethod. Runs every case, stops at the first failure, and ends the
    // Editor with exit code 0 only when all checks passed.
    public static void Run()
    {
        passed = 0;
        int exit = 1;
        try
        {
            CheckReply("HELLO OK " + VMDClient.ProtocolVersion, true, null);
            int differentVersion = VMDClient.ProtocolVersion == 1 ? 2 : 1;
            CheckReply("HELLO OK " + differentVersion, false, "does not match");
            // Malformed "HELLO OK" lines: a missing, non-numeric or extra word, a sign, an
            // overflowing number, a decimal, stray spaces, and a different second word.
            foreach (string reply in new[] {
                "HELLO OK garbage", "HELLO OK ", "HELLO OK", "HELLO OK 1 extra",
                "HELLO OK -1", "HELLO OK +1", "HELLO OK 2147483648", "HELLO OK 1.0",
                "HELLO OK 1 ", "HELLO OK  1", "HELLO OKAY 1"
            }) CheckReply(reply, false, "Invalid VMD bridge handshake reply");
            CheckReply("HELLO MISMATCH " + differentVersion, false, "does not match");
            // A bridge older than the handshake does not know HELLO.
            CheckReply("ERROR {Unknown command 'HELLO'}", false, "does not match");
            // The bridge's greeting is not a reply to HELLO; it must be left to the normal dispatch.
            CheckReply("READY VMD_VR_BRIDGE " + VMDClient.ProtocolVersion, false, null, false);
            Debug.Log("HANDSHAKE CHECKS: " + passed + " passed, 0 failed");
            exit = 0;
        }
        catch (Exception error) { Debug.LogException(error); }
        finally { EditorApplication.Exit(exit); }
    }
}
