using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using TwoWChat.Audio;
using TwoWChat.Core;

internal static partial class Program
{
    // Screen transport tests use only generated images. They never capture or
    // store the user's desktop, and require no screen/audio permission changes.
    private static byte[] SyntheticScreenJpeg(int phase = 0)
    {
        using var bitmap = new Bitmap(640, 360);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(phase % 2 == 0 ? Color.Navy : Color.DarkSlateGray);
            graphics.FillRectangle(Brushes.Gold, 20 + phase % 120, 30, 300, 80);
            graphics.FillEllipse(Brushes.SeaGreen, 360, 145, 220, 160);
            using var font = new Font("Tahoma", 24, FontStyle.Bold);
            graphics.DrawString("Screen " + phase, font, Brushes.White, 30, 180);
        }
        using var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Jpeg); return bytes.ToArray();
    }

    private static async Task CheckScreenSharingAsync()
    {
        using var identity = ServerIdentity.Create();
        await using var server = new RoomServer(identity, "Демонстрация", "screen-test");
        server.Start(0, IPAddress.Loopback);
        async Task<RoomClient> Join(string name)
        {
            var client = new RoomClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", server.Port, name, "screen-test", false, false, pin => pin == server.Fingerprint);
                return client;
            }
            catch { await client.DisposeAsync(); throw; }
        }
        await using var publisher = await Join("Ведущий");
        await using var first = await Join("Первый зритель");
        await using var second = await Join("Второй зритель");
        Check(publisher.SupportsScreenShare && first.SupportsScreenShare && second.SupportsScreenShare, "Screen sharing capability is negotiated by every client");
        Check(publisher.Screens.Length == 0 && first.Screens.Length == 0 && second.Screens.Length == 0, "Joining a room does not automatically start screen capture or a stream");
        var jpeg = SyntheticScreenJpeg();
        Check(!publisher.SendScreenFrame(jpeg), "Screen frames cannot be sent before explicitly starting a share");
        var firstFrames = new ConcurrentQueue<ScreenFrame>();
        var secondFrames = new ConcurrentQueue<ScreenFrame>();
        first.ScreenFrameReceived += firstFrames.Enqueue;
        second.ScreenFrameReceived += secondFrames.Enqueue;
        await publisher.StartScreenShareAsync("Тестовый монитор", 640, 360);
        var malformedRejected = false;
        try { publisher.SendScreenFrame(new byte[40]); } catch (InvalidDataException) { malformedRejected = true; }
        Check(malformedRejected && publisher.IsScreenSharing, "Malformed screen data is rejected locally without ending a valid share");
        var oversizedRejected = false;
        try { publisher.SendScreenFrame(new byte[ScreenWire.MaxFrameBytes + 1]); } catch (InvalidDataException) { oversizedRejected = true; }
        Check(oversizedRejected, "Oversized screen frame is rejected before entering the media queue");
        await Until(() => first.Screens.Any(s => s.OwnerId == publisher.Id) && second.Screens.Any(s => s.OwnerId == publisher.Id), "Starting a screen share advertises it to room participants");
        var advertised = first.Screens.Single(s => s.OwnerId == publisher.Id);
        Check(advertised.Name == "Ведущий" && advertised.Title == "Тестовый монитор" && advertised.Width == 640 && advertised.Height == 360, "Screen stream metadata uses authenticated owner and selected dimensions");
        await first.WatchScreenAsync(publisher.Id);
        await second.WatchScreenAsync(publisher.Id);
        var allFramesAccepted = true;
        for (var i = 0; i < 4; i++) { allFramesAccepted &= publisher.SendScreenFrame(jpeg); await Task.Delay(100); }
        Check(allFramesAccepted, "Active screen publisher accepts generated JPEG frames");
        await Until(() => !firstFrames.IsEmpty && !secondFrames.IsEmpty, "Two independent viewers receive live screen frames over media TLS sockets");
        Check(firstFrames.All(f => f.OwnerId == publisher.Id && f.Jpeg.SequenceEqual(jpeg)) && secondFrames.All(f => f.OwnerId == publisher.Id && f.Jpeg.SequenceEqual(jpeg)), "Screen JPEG bytes and owner identity survive relay unchanged");
        using (var frameBytes = new MemoryStream(firstFrames.First().Jpeg))
        using (var frame = Image.FromStream(frameBytes))
            Check(frame.Width == 640 && frame.Height == 360, "Relayed screen frame decodes to the expected visible image");

        async Task<bool> ForgedMediaRejected(Guid token)
        {
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate != null && ServerIdentity.Fingerprint(certificate) == server.Fingerprint);
            await tls.AuthenticateAsClientAsync("2WChat").WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Protocol.WriteAsync(tls, new WireMessage("screen_media")
            { Token = token, ScreenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Text = "watch", Id = publisher.Id }, timeout.Token);
            return (await Protocol.ReadAsync(tls, timeout.Token))?.Type == "error";
        }
        Check(await ForgedMediaRejected(Guid.NewGuid()), "A media socket without an authenticated room member is rejected");
        using (var rawTcp = new TcpClient())
        {
            await rawTcp.ConnectAsync(IPAddress.Loopback, server.Port);
            using var rawTls = new SslStream(rawTcp.GetStream(), false, (_, certificate, _, _) => certificate != null && ServerIdentity.Fingerprint(certificate) == server.Fingerprint);
            await rawTls.AuthenticateAsClientAsync("2WChat").WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Protocol.WriteAsync(rawTls, new WireMessage("hello") { Name = "Проверка доступа", Password = "screen-test", SupportsScreenShare = true }, timeout.Token);
            var welcome = await Protocol.ReadAsync(rawTls, timeout.Token) ?? throw new IOException("Authentication test welcome missing");
            Check(welcome.Type == "welcome" && await ForgedMediaRejected(welcome.Token), "A valid public session token without the private screen secret cannot open media");
            await Protocol.WriteAsync(rawTls, new WireMessage("ping") { Stamp = 12345 }, timeout.Token);
            WireMessage? pong = null;
            while (pong?.Type != "pong") pong = await Protocol.ReadAsync(rawTls, timeout.Token) ?? throw new IOException("Room closed after forged media authentication");
            Check(pong.Stamp == 12345, "Rejected media authentication leaves the legitimate main room connection usable");
        }

        var competingRejected = false;
        try { await first.StartScreenShareAsync("Конкурент", 640, 360); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { competingRejected = true; }
        Check(competingRejected && first.Connected, "A competing publisher is rejected while its chat session remains connected");
        await Until(() => publisher.UdpReady && first.UdpReady, "Voice UDP path remains available during screen watching");
        var texts = new ConcurrentQueue<ChatEntry>();
        second.Message += message => { if (message.Chat != null) texts.Enqueue(message.Chat); };
        var voiceFrames = 0;
        first.Voice += _ => Interlocked.Increment(ref voiceFrames);
        using var encoder = new VoiceEncoder();
        for (uint frame = 0; frame < 30; frame++)
        {
            publisher.SendVoice(frame, 40, encoder.Encode(Tone((int)frame)));
            if (frame % 5 == 0) publisher.SendScreenFrame(jpeg);
            if (frame == 5) await publisher.SendChatAsync("Голос и текст во время демонстрации.");
            await Task.Delay(20);
        }
        await Until(() => voiceFrames >= 20 && texts.Any(t => t.Text.Contains("во время демонстрации")), "Voice and text continue while a live screen stream is transmitting");

        await first.StopWatchingScreenAsync();
        await Task.Delay(150);
        var firstBefore = firstFrames.Count;
        var laterJpeg = SyntheticScreenJpeg(1);
        for (var i = 0; i < 4; i++) { publisher.SendScreenFrame(laterJpeg); await Task.Delay(100); }
        await Until(() => secondFrames.Any(f => f.Jpeg.SequenceEqual(laterJpeg)), "Remaining viewer continues receiving after another viewer stops watching");
        Check(firstFrames.Count == firstBefore && first.Connected, "Stopped viewer receives no more screen frames and stays in text chat");

        await using var late = await Join("Поздний зритель");
        Check(late.Screens.Any(s => s.OwnerId == publisher.Id), "Late joiner learns about the active screen share in welcome metadata");
        var lateFrames = new ConcurrentQueue<ScreenFrame>(); late.ScreenFrameReceived += lateFrames.Enqueue;
        await late.WatchScreenAsync(publisher.Id);
        for (var i = 0; i < 3; i++) { publisher.SendScreenFrame(laterJpeg); await Task.Delay(100); }
        await Until(() => lateFrames.Any(f => f.OwnerId == publisher.Id), "Late joiner can watch the existing screen stream");

        await publisher.StopScreenShareAsync();
        await Until(() => first.Screens.Length == 0 && second.Screens.Length == 0 && late.Screens.Length == 0, "Stopping a screen share removes it from all participant lists");
        Check(!publisher.SendScreenFrame(jpeg) && publisher.Connected, "Stopped publisher cannot send screen frames but remains connected to the room");
        var rapidRestartsStable = true;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            // Deliberately do not wait for an empty control snapshot between
            // operations: the public stop/start API must handle that race.
            await publisher.StartScreenShareAsync("Повторный запуск " + cycle, 640, 360);
            await second.WatchScreenAsync(publisher.Id);
            var cycleJpeg = SyntheticScreenJpeg(10 + cycle);
            rapidRestartsStable &= publisher.SendScreenFrame(cycleJpeg);
            await Until(() => secondFrames.Any(f => f.OwnerId == publisher.Id && f.Jpeg.SequenceEqual(cycleJpeg)),
                "Rapid same-owner restart " + (cycle + 1) + " delivers its new screen frame");
            rapidRestartsStable &= publisher.IsScreenSharing;
            await publisher.StopScreenShareAsync();
        }
        Check(rapidRestartsStable && publisher.Connected && second.Connected, "Ten rapid screen stop/start cycles survive stale control snapshots without disconnecting chat");
        var unknownRejected = false;
        try { await first.WatchScreenAsync(Guid.NewGuid()); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { unknownRejected = true; }
        Check(unknownRejected && first.Connected, "Watching a nonexistent owner is rejected without breaking chat");

        await first.StartScreenShareAsync("Новая демонстрация", 640, 360);
        await Until(() => second.Screens.Any(s => s.OwnerId == first.Id), "Another participant can become publisher after the previous share stops");
        await second.WatchScreenAsync(first.Id);
        var newJpeg = SyntheticScreenJpeg(2);
        for (var i = 0; i < 3; i++) { first.SendScreenFrame(newJpeg); await Task.Delay(100); }
        await Until(() => secondFrames.Any(f => f.OwnerId == first.Id && f.Jpeg.SequenceEqual(newJpeg)), "Viewer can switch to the new screen publisher");
        await first.DisposeAsync();
        await Until(() => second.Screens.Length == 0 && late.Screens.Length == 0, "Publisher disconnect automatically closes the screen share for every viewer");
        await publisher.SendChatAsync("После завершения демонстрации.");
        await Until(() => texts.Any(t => t.Text == "После завершения демонстрации."), "Room chat remains usable after screen media connections are cleaned up");
    }
}
