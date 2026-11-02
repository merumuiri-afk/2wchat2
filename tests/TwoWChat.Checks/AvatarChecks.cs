using System.Drawing.Imaging;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using TwoWChat;
using TwoWChat.Core;
using TwoWChat.Images;

internal static partial class Program
{
    private static async Task CheckAvatarsAsync()
    {
        using var source = new Bitmap(500, 220);
        using (var g = Graphics.FromImage(source)) { g.Clear(Color.Navy); g.FillEllipse(Brushes.Gold, 160, 10, 200, 200); }
        var avatar = AvatarPreparation.FromImage(source);
        Check(AvatarData.IsValid(avatar) && avatar.Length <= AvatarData.MaxBytes, "Avatar preparation creates a bounded baseline JPEG");
        using (var rendered = AvatarPreparation.Render(avatar, "Test", 96))
            Check(rendered.Width == 96 && rendered.Height == 96 && rendered.GetPixel(48, 48).R > 150, "Avatar is square, centered and decodes to the expected pixels");
        Check(AvatarData.IsValid([]) && !AvatarData.IsValid(new byte[AvatarData.MaxBytes + 1]) && !AvatarData.IsValid([1, 2, 3]), "Avatar validation supports removal and rejects invalid or oversized bytes");
        using (var large = new MemoryStream())
        {
            source.Save(large, ImageFormat.Jpeg);
            Check(!AvatarData.IsValid(large.ToArray()), "Remote avatars cannot advertise oversized pixel dimensions");
        }
        using (var fallback = AvatarPreparation.Render([1, 2, 3], "Я", 32))
            Check(fallback.Width == 32, "Malformed remote image uses a safe initials placeholder");
        var settings = new AppSettings { Avatar = avatar };
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        clone.Normalize(); Check(clone.Avatar!.SequenceEqual(avatar), "Avatar survives profile serialization and normalization");
        clone.Avatar = [1, 2, 3]; clone.Normalize(); Check(clone.Avatar == null, "Invalid saved avatar is removed during profile migration");
        using var identity = ServerIdentity.Create();
        await using var server = new RoomServer(identity, "Avatars", ""); server.Start(0, IPAddress.Loopback);
        async Task<RoomClient> Join(string name)
        {
            var client = new RoomClient(); await client.ConnectAsync("127.0.0.1", server.Port, name, "", false, false, p => p == server.Fingerprint); return client;
        }
        await using var a = await Join("Author"); await using var b = await Join("Viewer");
        server.Rename("Новое название");
        await Until(() => a.Room == "Новое название" && b.Room == "Новое название", "Owner-approved rename updates all connected clients");
        var gifBytes = Convert.FromHexString("47494638396101000100800000000000FFFFFF21F904000A0000002C000000000100010000020244010021F904000A0000002C00000000010001000002024C01003B");
        var gifPath = Path.Combine(_reportFolder, "animated.gif"); File.WriteAllBytes(gifPath, gifBytes);
        var gif = ImagePreparation.FromFile(gifPath);
        using (var animated = ImagePreparation.OpenAnimation(gif))
        {
            Check(animated.GetFrameCount(FrameDimension.Time) == 2 && gif.Data.SequenceEqual(gifBytes), "Animated GIF keeps original bytes and both frames");
            animated.SelectActiveFrame(FrameDimension.Time, 0); var first = new Bitmap(animated).GetPixel(0, 0);
            animated.SelectActiveFrame(FrameDimension.Time, 1); using var second = new Bitmap(animated);
            Check(first != second.GetPixel(0, 0), "GIF frames decode to different visible pixels");
            (animated.Tag as Stream)?.Dispose();
        }
        var delivered = new TaskCompletionSource<ImagePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ImageReceived += p => { if (p.Info.ContentType == "image/gif") delivered.TrySetResult(p); };
        await a.SendImageAsync(gif, "Animated GIF");
        Check((await delivered.Task.WaitAsync(TimeSpan.FromSeconds(8))).Data.SequenceEqual(gifBytes), "Animated GIF crosses TLS image transport without flattening");
        Check(a.SupportsAvatars && b.SupportsAvatars, "Room negotiates avatar capability separately from chat and images");
        await a.SetAvatarAsync(avatar);
        await Until(() => b.GetAvatar(a.Id)?.SequenceEqual(avatar) == true && a.GetAvatar(a.Id) != null, "Avatar reaches owner and another participant with identical bytes");
        await using var late = await Join("Late");
        Check(late.Room == "Новое название", "Late joiner receives the approved room name");
        await Until(() => late.GetAvatar(a.Id)?.SequenceEqual(avatar) == true, "Late joiner receives avatars after its bounded welcome frame");
        await a.SendAsync(new WireMessage("avatar") { Id = b.Id, Data = avatar });
        await a.SendChatAsync("avatar identity probe");
        await Task.Delay(150);
        Check(b.GetAvatar(b.Id) == null, "Server replaces spoofed avatar owner with authenticated sender identity");
        await a.SendAsync(new WireMessage("avatar") { Data = [1, 2, 3] });
        await a.SendChatAsync("after invalid avatar"); await Task.Delay(150);
        Check(a.Connected && b.Connected && b.GetAvatar(a.Id)!.SequenceEqual(avatar), "Invalid avatar leaves the previous image and room connection intact");
        await a.SetAvatarAsync([]);
        await Until(() => b.GetAvatar(a.Id) == null && late.GetAvatar(a.Id) == null, "Avatar removal propagates to all participants");
        await Task.Delay(1100); await a.SetAvatarAsync(avatar);
        await Until(() => b.GetAvatar(a.Id) != null, "Avatar can be set again after removal");
        await a.DisposeAsync();
        await Until(() => b.Members.All(p => p.Id != a.Id) && b.GetAvatar(a.Id) == null, "Departed participant avatar is evicted from the client cache");
        // Legacy client advertises no avatar capability; no new command is sent to it.
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true); await ssl.AuthenticateAsClientAsync("2WChat");
        await Protocol.WriteAsync(ssl, new WireMessage("hello") { Name = "Legacy" }, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var welcome = await Protocol.ReadAsync(ssl, timeout.Token);
        await b.SetAvatarAsync(avatar); await b.SendChatAsync("legacy marker");
        WireMessage? packet; var received = false;
        while ((packet = await Protocol.ReadAsync(ssl, timeout.Token)) != null)
        {
            Check(packet.Type != "avatar", "Legacy client is not sent unsupported avatar commands");
            if (packet.Chat?.Text == "legacy marker") { received = true; break; }
        }
        Check(welcome?.Type == "welcome" && received, "Legacy client continues receiving text in an avatar-enabled room");
    }
}
