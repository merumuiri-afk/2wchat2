using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TwoWChat.Core;
using TwoWChat.Images;

internal static partial class Program
{
    private static ImagePayload FixtureImage(int width = 128, int height = 72, bool noise = false)
    {
        using var bitmap = new Bitmap(width, height);
        if (noise)
        {
            var pixels = new byte[width * height * 4]; new Random(1234).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length); } finally { bitmap.UnlockBits(locked); }
        }
        else
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.CornflowerBlue);
            graphics.FillRectangle(Brushes.Gold, width / 4, height / 4, width / 2, height / 2);
        }
        return ImagePreparation.FromBitmap(bitmap, "Проверка.png");
    }
    private static async Task CheckImagesAsync()
    {
        var image = FixtureImage(1024, 1024, true);
        Check(image.Data.Length > Protocol.ImageChunkBytes * 128, "Image fixture spans more than 128 transport chunks");
        ImageAttachments.Validate(image);
        using (var decoded = ImagePreparation.Decode(image)) Check(decoded.Width == 1024 && decoded.Height == 1024, "Prepared PNG retains dimensions and can be decoded");
        using (var thumbnail = ImagePreparation.Thumbnail(image)) Check(thumbnail.Width <= 320 && thumbnail.Height <= 220, "Image thumbnail is bounded to the chat preview size");
        var folder = Path.Combine(_reportFolder, "images"); Directory.CreateDirectory(folder);
        using (var source = new Bitmap(96, 64))
        {
            using (var g = Graphics.FromImage(source)) g.Clear(Color.OrangeRed);
            foreach (var (name, format) in new[] { ("photo.jpg", ImageFormat.Jpeg), ("first-frame.gif", ImageFormat.Gif), ("bitmap.bmp", ImageFormat.Bmp) })
            {
                var file = Path.Combine(folder, name); source.Save(file, format);
                var prepared = ImagePreparation.FromFile(file); ImageAttachments.Validate(prepared);
                Check(prepared.Info.Width == 96 && prepared.Info.Height == 64, name + " is prepared as a supported image");
            }
        }
        var rejected = false;
        try { ImageAttachments.Validate(image with { Info = image.Info with { Sha256 = new string('0', 64) } }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Image checksum mismatch is rejected");
        rejected = false;
        try { ImageAttachments.ValidateInfo(image.Info with { Length = Protocol.MaxImageBytes + 1 }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Image size cap is checked before transfer allocation");
        rejected = false;
        try { ImageAttachments.ValidateInfo(image.Info with { Width = 100000 }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Oversized pixel dimensions are rejected");
        rejected = false;
        try { ImageAttachments.Create("<svg><script>test</script></svg>"u8.ToArray(), "image.svg"); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Non-raster payload is not accepted as an image");

        using var cert = ServerIdentity.Create(); await using var server = new RoomServer(cert, "Картинки", ""); server.Start(0, IPAddress.Loopback);
        async Task<RoomClient> Join(string name)
        {
            var client = new RoomClient(); await client.ConnectAsync("127.0.0.1", server.Port, name, "", false, false, p => p == server.Fingerprint); return client;
        }
        await using var a = await Join("Автор"); await using var b = await Join("Получатель");
        Check(a.SupportsImages && b.SupportsImages, "Room negotiates image support with both clients");
        var received = new ConcurrentQueue<ChatEntry>(); b.Message += m => { if (m.Chat != null) received.Enqueue(m.Chat); };
        using var oldTcp = new TcpClient(); await oldTcp.ConnectAsync(IPAddress.Loopback, server.Port);
        using var oldTls = new SslStream(oldTcp.GetStream(), false, (_, c, _, _) => c != null && ServerIdentity.Fingerprint(c) == server.Fingerprint);
        await oldTls.AuthenticateAsClientAsync("2WChat");
        await Protocol.WriteAsync(oldTls, new WireMessage("hello") { Name = "Старый клиент", SupportsImages = false }, default);
        Check((await Protocol.ReadAsync(oldTls, default))?.Type == "welcome", "Client without image support can still join the room");
        await Until(() => a.UdpReady && b.UdpReady, "UDP remains available in image-capable sessions");
        var progress = new List<int>();
        var upload = a.SendImageAsync(image with { Info = image.Info with { FileName = "..\\..\\unsafe.svg" } }, "Картинка с подписью", new InlineProgress(value => { progress.Add(value); Thread.Sleep(2); }));
        await b.SendChatAsync("Текст во время передачи картинки.");
        await Until(() => received.Any(c => c.Text.StartsWith("Текст во время")), "Text is delivered while a large image is uploading");
        Check(!upload.IsCompleted, "Control messages do not wait for the complete image upload");
        using var encoder = new VoiceEncoder();
        for (uint frame = 0; frame < 30; frame++) { b.SendVoice(frame, 40, encoder.Encode(Tone((int)frame))); await Task.Delay(20); }
        var chat = await upload.WaitAsync(TimeSpan.FromSeconds(15)); var id = chat.Image!.Id;
        await Until(() => a.GetImage(id) != null && b.GetImage(id) != null, "Sender and recipient download the complete chunked image", 15000);
        Check(a.GetImage(id)!.Data.SequenceEqual(image.Data) && b.GetImage(id)!.Data.SequenceEqual(image.Data), "Image bytes are identical at sender, host and recipient");
        Check(chat.Sender == a.Id && chat.Name == "Автор" && chat.Text == "Картинка с подписью", "Image message uses authenticated sender and preserves caption");
        Check(chat.Image.FileName == "unsafe.png", "Remote file paths and mismatched extensions are normalized");
        Check(a.VoicePacketsReceived >= 20 && a.Connected && b.Connected, "Opus voice and room connection continue during image transfer");
        Check(progress.First() == 0 && progress.Last() == 100 && progress.SequenceEqual(progress.Order()), "Image upload progress runs monotonically from zero to complete");
        WireMessage? legacyChat = null;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (legacyChat?.Chat?.Id != chat.Id) legacyChat = await Protocol.ReadAsync(oldTls, timeout.Token);
        Check(legacyChat.Type == "chat" && legacyChat.Chat.Text == "Картинка с подписью", "Older client receives an image caption as a normal chat message");
        await using var late = await Join("Поздний");
        Check(late.History.Any(c => c.Image?.Id == id), "Late joiner receives image metadata in room history");
        await Until(() => late.GetImage(id) != null, "Late joiner retrieves recent image bytes without oversized welcome packets", 15000);

        await using var malformed = await Join("Повреждённый"); var small = FixtureImage();
        var corruptInfo = small.Info with { Id = Guid.NewGuid() };
        var corruptBytes = small.Data.ToArray(); corruptBytes[^1] ^= 1;
        await malformed.SendAsync(new WireMessage("image_start") { Id = corruptInfo.Id, Image = corruptInfo });
        await malformed.SendAsync(new WireMessage("image_chunk") { Id = corruptInfo.Id, Offset = 0, Data = corruptBytes });
        await malformed.SendAsync(new WireMessage("image_finish") { Id = corruptInfo.Id });
        var orderInfo = small.Info with { Id = Guid.NewGuid() };
        await malformed.SendAsync(new WireMessage("image_start") { Id = orderInfo.Id, Image = orderInfo });
        await malformed.SendAsync(new WireMessage("image_chunk") { Id = orderInfo.Id, Offset = 1, Data = small.Data });
        await malformed.SendChatAsync("После ошибок картинки чат работает.");
        await Until(() => received.Any(c => c.Text.StartsWith("После ошибок")), "Corrupt or out-of-order images do not disconnect text chat");
        Check(received.All(c => c.Sender != malformed.Id || c.Image == null), "Corrupt and incomplete images are never published in chat");
        await using var canceled = await Join("Отмена"); using var cancel = new CancellationTokenSource();
        var canceledCleanly = false;
        try { await canceled.SendImageAsync(image, progress: new InlineProgress(value => { if (value >= 25) cancel.Cancel(); }), cancellationToken: cancel.Token); }
        catch (OperationCanceledException) { canceledCleanly = true; }
        await canceled.SendChatAsync("После отмены отправки.");
        await Until(() => received.Any(c => c.Text == "После отмены отправки."), "Canceled upload leaves the room usable");
        Check(canceledCleanly && received.All(c => c.Sender != canceled.Id || c.Image == null), "Canceled image is not published as a partial attachment");
    }
    private sealed class InlineProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
}
