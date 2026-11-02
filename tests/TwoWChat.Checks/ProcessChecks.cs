using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TwoWChat.Core;

internal static partial class Program
{
    private sealed record ProbeInfo(int Port, string Fingerprint);
    private sealed record ProbeResult(bool Passed, int Chats, int VoicePackets, bool Udp, string? Error);

    private static Process StartProbe(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        // The checker may be launched with dotnet or its own apphost.
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Probe could not be started");
    }

    private static async Task CheckProcessesAsync()
    {
        var directory = Path.Combine(_reportFolder, "processes-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var server = StartProbe("--probe-server", directory);
        try
        {
            await Until(() => File.Exists(Path.Combine(directory, "server.json")), "Separate server process binds TCP and UDP");
            using var a = StartProbe("--probe-peer", directory, "A");
            using var b = StartProbe("--probe-peer", directory, "B");
            await Task.WhenAll(a.WaitForExitAsync(), b.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15));
            var first = JsonSerializer.Deserialize<ProbeResult>(File.ReadAllText(Path.Combine(directory, "peer-A.json")))!;
            var second = JsonSerializer.Deserialize<ProbeResult>(File.ReadAllText(Path.Combine(directory, "peer-B.json")))!;
            Check(a.ExitCode == 0 && b.ExitCode == 0 && first.Passed && second.Passed, "Independent client processes exchange chat and Opus voice through the server");
            Check(first.VoicePackets >= 70 && second.VoicePackets >= 70 && first.Udp && second.Udp, "Voice runs bidirectionally across three OS processes");
            Check(first.Chats >= 2 && second.Chats >= 2, "Every independent process receives both participants' messages");
            Notes.Add($"Cross-process voice: A={first.VoicePackets}, B={second.VoicePackets} packets");
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "stop"), "stop");
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(server.ExitCode == 0, "Separate server process shuts down without errors");
    }

    private static async Task ProbeServerAsync(string directory)
    {
        using var cert = ServerIdentity.Create(); await using var server = new RoomServer(cert, "Process test", "probe");
        server.Start(0, IPAddress.Loopback);
        var temporary = Path.Combine(directory, "server.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new ProbeInfo(server.Port, server.Fingerprint)));
        File.Move(temporary, Path.Combine(directory, "server.json"), true);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "stop")) && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
    }

    private static async Task ProbePeerAsync(string directory, string name)
    {
        var chats = 0; var voice = 0; bool udp = false; string? error = null;
        try
        {
            var info = JsonSerializer.Deserialize<ProbeInfo>(File.ReadAllText(Path.Combine(directory, "server.json")))!;
            await using var client = new RoomClient();
            client.Message += m => { if (m.Chat != null) Interlocked.Increment(ref chats); };
            client.Voice += f => { using var decoder = new VoiceDecoder(); var pcm = new short[960]; if (decoder.Decode(f.Opus, pcm) == 960) Interlocked.Increment(ref voice); };
            await client.ConnectAsync("127.0.0.1", info.Port, name, "probe", false, false, p => p == info.Fingerprint);
            var watch = Stopwatch.StartNew();
            while ((client.Members.Length < 2 || !client.UdpReady) && watch.ElapsedMilliseconds < 5000) await Task.Delay(20);
            await client.SendChatAsync("Process " + name + ": привет!");
            using var encoder = new VoiceEncoder();
            for (uint i = 0; i < 100; i++) { client.SendVoice(i, 30, encoder.Encode(Tone((int)i))); await Task.Delay(20); }
            await Task.Delay(500); udp = client.UdpReady;
        }
        catch (Exception e) { error = e.ToString(); }
        var result = new ProbeResult(error == null && chats >= 2 && voice >= 70 && udp, chats, voice, udp, error);
        File.WriteAllText(Path.Combine(directory, "peer-" + name + ".json"), JsonSerializer.Serialize(result));
        Environment.ExitCode = result.Passed ? 0 : 1;
    }
}
