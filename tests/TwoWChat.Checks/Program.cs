using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using TwoWChat;
using TwoWChat.Audio;
using TwoWChat.Core;

internal static partial class Program
{
    private static readonly List<string> Checks = [];
    private static readonly List<string> Notes = [];
    private static readonly List<string> Errors = [];
    private static string _reportFolder = "";
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Checks.Add(description); Console.WriteLine("PASS " + description);
    }
    private static async Task Until(Func<bool> condition, string description, int timeout = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException(description); await Task.Delay(20); }
        Check(true, description);
    }
    private static async Task Main(string[] args)
    {
        if (args.FirstOrDefault() == "--capture-device") { await CheckCaptureDeviceAsync(); return; }
        if (args.FirstOrDefault() == "--probe-server") { await ProbeServerAsync(args[1]); return; }
        if (args.FirstOrDefault() == "--probe-peer") { await ProbePeerAsync(args[1], args[2]); return; }
        _reportFolder = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/checks");
        Directory.CreateDirectory(_reportFolder);
        var watch = Stopwatch.StartNew();
        try
        {
            CheckCipher(); await CheckFramingAsync(); CheckAudioAlgorithms();
            CheckNotificationAsset(); CheckSettings(); await CheckRoomAsync();
            await CheckUpdatesAsync();
            await CheckImagesAsync();
            await CheckScreenSharingAsync();
            await CheckScreenCaptureAsync();
            await CheckProcessesAsync();
            if (args.Contains("--audio")) await CheckHardwareAsync();
        }
        catch (Exception e) { Errors.Add(e.ToString()); Console.Error.WriteLine(e); }
        watch.Stop();
        File.WriteAllText(Path.Combine(_reportFolder, "checks.json"), JsonSerializer.Serialize(new { Passed = Errors.Count == 0, Count = Checks.Count, Seconds = watch.Elapsed.TotalSeconds, Checks, Notes, Errors }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{Checks.Count} checks; {Errors.Count} failures; {watch.Elapsed.TotalSeconds:F1}s");
        Environment.ExitCode = Errors.Count == 0 ? 0 : 1;
    }

    private static void CheckCipher()
    {
        var key = RandomNumberGenerator.GetBytes(32); var token = Guid.NewGuid();
        using var client = new UdpCipher(token, key, true); using var server = new UdpCipher(token, key, false);
        var input = "encrypted voice"u8.ToArray();
        var packet = client.Seal(1, input);
        Check(!packet.AsSpan().IndexOf(input).Equals(30), "UDP payload is encrypted");
        Check(server.TryOpen(packet, out var kind, out var plain) && kind == 1 && plain.SequenceEqual(input), "AES-GCM client to server round trip");
        Check(!server.TryOpen(packet, out _, out _), "Replayed UDP packet is rejected");
        Check(!client.TryOpen(packet, out _, out _), "Opposite nonce domains prevent reflected packets");
        packet = client.Seal(1, input); packet[^1] ^= 1;
        Check(!server.TryOpen(packet, out _, out _), "Modified authentication tag is rejected");
        packet = client.Seal(1, input); packet[5] ^= 1;
        Check(!server.TryOpen(packet, out _, out _), "UDP header is authenticated");
        var early = client.Seal(1, input); var late = client.Seal(1, input);
        Check(server.TryOpen(late, out _, out _) && server.TryOpen(early, out _, out _), "Reordered UDP packets are accepted once");
        var expired = client.Seal(1, input);
        for (var i = 0; i < 70; i++) { var next = client.Seal(0, []); server.TryOpen(next, out _, out _); }
        Check(!server.TryOpen(expired, out _, out _), "Packets outside the replay window are rejected");
        using var wrong = new UdpCipher(token, RandomNumberGenerator.GetBytes(32), false);
        Check(!wrong.TryOpen(client.Seal(0, []), out _, out _), "Another session key cannot authenticate a datagram");
        Check(client.TryOpen(server.Seal(0, []), out kind, out plain) && kind == 0 && plain.Length == 0, "Encrypted UDP keepalive works in reverse direction");
        Check(!UdpCipher.TryGetToken(new byte[4], out _), "Truncated UDP header rejected");
        var frame = new VoiceFrame(Guid.NewGuid(), 345, 15, [1, 2, 3]);
        var restored = UdpCipher.UnpackVoice(UdpCipher.PackVoice(frame));
        Check(restored != null && restored.Speaker == frame.Speaker && restored.Sequence == frame.Sequence && restored.Opus.SequenceEqual(frame.Opus), "Voice framing preserves speaker, sequence, level and Opus bytes");
    }

    private static async Task CheckFramingAsync()
    {
        using var memory = new MemoryStream();
        await Protocol.WriteAsync(memory, new WireMessage("chat") { Text = "Привет 👋\nТекст \"с кавычками\"" }, default);
        memory.Position = 0;
        using var fragmented = new FragmentedStream(memory.ToArray());
        var result = await Protocol.ReadAsync(fragmented, default);
        Check(result?.Text == "Привет 👋\nТекст \"с кавычками\"", "Length framing handles fragmented Unicode messages");
        Check(await Protocol.ReadAsync(fragmented, default) == null, "Clean end of TCP stream is handled");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 1000000);
        var rejected = false;
        try { await Protocol.ReadAsync(new MemoryStream(header), default); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Oversized TCP frame rejected before allocation");
        Check(Protocol.CleanName("  Ва\nся\u202e  ") == "Вася", "Names strip control and bidi formatting characters");
        rejected = false; try { Protocol.CleanText(new string('a', 2001)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Text length is bounded");
    }

    private static short[] Tone(int frame, double frequency = 440, double amplitude = 6000) => Enumerable.Range(0, Protocol.FrameSamples)
        .Select(i => (short)(Math.Sin((frame * Protocol.FrameSamples + i) * 2 * Math.PI * frequency / Protocol.SampleRate) * amplitude)).ToArray();
    private static double Rms(short[] data) => Math.Sqrt(data.Average(x => (double)x * x));

    private static void CheckAudioAlgorithms()
    {
        using var encoder = new VoiceEncoder(); using var decoder = new VoiceDecoder();
        var watch = Stopwatch.StartNew(); var bytes = 0; double energy = 0;
        var packets = new List<byte[]>(); var decoded = new short[960];
        for (var i = 0; i < 100; i++)
        {
            var packet = encoder.Encode(Tone(i)); packets.Add(packet); bytes += packet.Length;
            if (decoder.Decode(packet, decoded) != 960) throw new InvalidOperationException("Codec did not decode 20ms");
            energy += Rms(decoded);
        }
        watch.Stop();
        Check(energy / 100 > 2000, "100 Opus frames decode to an audible signal");
        Check(bytes < 100 * 1920 / 3, "Opus compresses voice well below raw PCM size");
        Check(decoder.Decode(null, decoded) == 960, "Opus packet loss concealment returns a full frame");
        Check(decoder.Decode(packets[^1], decoded, true) == 960, "Opus forward error correction accepts a next packet");
        Check(watch.ElapsedMilliseconds < 2000, "Encoding and decoding are faster than real time");
        Notes.Add($"Codec {encoder.Implementation}; 100 frames in {watch.Elapsed.TotalMilliseconds:F1}ms; {bytes} compressed bytes");
        using var jitter = new VoiceJitterBuffer(60); using var reference = new VoiceDecoder();
        jitter.Add(10, packets[0]); jitter.Add(12, packets[2]); jitter.Add(11, packets[1]);
        var ordered = new short[960]; var actual = new short[960]; var equal = true;
        for (var i = 0; i < 3; i++) { reference.Decode(packets[i], ordered); jitter.ReadFrame(actual); equal &= ordered.SequenceEqual(actual); }
        Check(equal, "Jitter buffer reorders three Opus frames into original audio");
        jitter.Add(14, packets[4]); jitter.ReadFrame(actual); jitter.ReadFrame(actual);
        Check(jitter.LostFrames == 1 && Rms(actual) > 100, "Missing frame is recovered and playback continues");
        for (uint i = 100; i < 140; i++) jitter.Add(i, packets[(int)(i % 100)]);
        Check(jitter.BufferedFrames <= 12, "Playback buffer remains bounded after a burst");
        var filter = new MicrophoneFilter();
        for (var i = 0; i < 30; i++) filter.Process(new short[960], 1, true, -42);
        Check(!filter.GateOpen, "Noise gate closes on silence");
        var speech = Tone(0); filter.Process(speech, 1, true, -42);
        Check(filter.GateOpen && Rms(speech) > 1000, "Noise gate opens for speech-level input");
        for (var i = 0; i < 40; i++) filter.Process(new short[960], 1, true, -42);
        Check(!filter.GateOpen, "Noise gate releases after a pause");
        using var mixer = new VoiceMixer(40); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        mixer.Add(new VoiceFrame(a, 0, 50, packets[0])); mixer.Add(new VoiceFrame(a, 1, 50, packets[1]));
        mixer.Add(new VoiceFrame(b, 0, 50, packets[0])); mixer.Add(new VoiceFrame(b, 1, 50, packets[1]));
        var buffer = new byte[3840]; mixer.Read(buffer, 0, buffer.Length);
        Check(buffer.Any(x => x != 0) && mixer.ActiveStreams == 2, "Mixer renders simultaneous participants");
        mixer.Deafened = true; mixer.Read(buffer, 0, buffer.Length);
        Check(buffer.All(x => x == 0), "Deafen silences the complete playback mix");
        mixer.SyncMembers([a]); Check(mixer.ActiveStreams == 1, "Departed participant audio resources are released");
    }

    private static void CheckSettings()
    {
        var folder = Path.Combine(_reportFolder, "settings-test"); AppStore.SetDataDirectory(folder);
        var settings = new AppSettings { Name = "Сохранённый", Host = "26.1.2.3", InputGain = 500, NoiseGate = true, TalkKey = (int)Keys.F9 };
        settings.TrustedHosts["26.1.2.3:48500"] = "012345";
        AppStore.Save(settings); var loaded = AppStore.Load();
        Check(loaded.Name == settings.Name && loaded.Host == settings.Host && loaded.NoiseGate && loaded.TalkKey == (int)Keys.F9, "Device and connection settings survive restart");
        Check(loaded.InputGain == 200, "Invalid setting values are normalized");
        Check(loaded.MessageNotifications && loaded.MessageNotificationSound, "Message notifications and their sound are enabled by default");
        Check(loaded.TrustedHosts["26.1.2.3:48500"] == "012345", "Accepted host fingerprints are persisted");
        using var first = AppStore.Identity(); using var second = AppStore.Identity();
        Check(ServerIdentity.Fingerprint(first) == ServerIdentity.Fingerprint(second), "Room certificate is stable across restarts");
        Check(!File.ReadAllText(Path.Combine(folder, "settings.json")).Contains("password", StringComparison.OrdinalIgnoreCase), "Room passwords are not saved in settings");
        loaded.MessageNotifications = false; loaded.MessageNotificationSound = false; AppStore.Save(loaded);
        var quiet = AppStore.Load();
        Check(!quiet.MessageNotifications && !quiet.MessageNotificationSound, "Disabled notification preferences survive restart");
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"Name\":\"Old profile\",\"OutputVolume\":60}");
        var legacy = AppStore.Load();
        Check(legacy.MessageNotifications && legacy.MessageNotificationSound && legacy.OutputVolume == 60, "Existing 1.0 profiles gain notification defaults without losing audio settings");
        Check(legacy.NotificationVolume == 60, "Older profiles inherit their previous notification volume on upgrade");
        legacy.NotificationVolume = 35; legacy.OutputVolume = 90; AppStore.Save(legacy);
        var independent = AppStore.Load();
        Check(independent.NotificationVolume == 35 && independent.OutputVolume == 90, "Notification and voice volumes persist independently");
        independent.OutputVolume = 0;
        Check(independent.EffectiveNotificationVolume == 35, "Muting voice volume does not change notification volume");
        independent.NotificationVolume = 500; independent.Normalize();
        Check(independent.NotificationVolume == 150, "Notification volume is bounded to the supported range");
    }

    private static void CheckNotificationAsset()
    {
        using var wave = MessageNotificationSound.OpenEmbeddedWave();
        Check(wave.WaveFormat.SampleRate == 48000 && wave.WaveFormat.BitsPerSample == 16 && wave.WaveFormat.Channels == 2, "Embedded notification is fully decoded stereo PCM");
        Check(wave.TotalTime.TotalSeconds is > 1.3 and < 1.4 && wave.Length == 258048, "Embedded notification preserves the supplied clip's complete duration");
        var pcm = new byte[wave.Length]; wave.ReadExactly(pcm);
        Check(pcm.Any(b => b != 0), "Embedded notification contains audio rather than silence");
    }

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}
