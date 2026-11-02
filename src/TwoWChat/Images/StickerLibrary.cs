using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TwoWChat.Core;

namespace TwoWChat.Images;

public static class StickerLibrary
{
    public static string Folder => Path.Combine(AppStore.Folder, "stickers");
    public static string[] Files() => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
    public static ImagePayload Prepare(ImagePayload source, bool removeBackground = false)
    {
        using var decoded = ImagePreparation.Decode(source);
        var scale = Math.Min(1d, 320d / Math.Max(decoded.Width, decoded.Height));
        using var bitmap = new Bitmap(Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.Transparent); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(decoded, new Rectangle(Point.Empty, bitmap.Size)); }
        if (removeBackground) ClearEdgeBackground(bitmap);
        using var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Png);
        var payload = ImageAttachments.Create(bytes.ToArray(), "Стикер.png");
        return payload with { Info = payload.Info with { IsSticker = true } };
    }
    private static void ClearEdgeBackground(Bitmap image)
    {
        // Flood only edge-connected pixels similar to the top-left background.
        var background = image.GetPixel(0, 0); var seen = new bool[image.Width * image.Height]; var pending = new Queue<Point>();
        for (var x = 0; x < image.Width; x++) { pending.Enqueue(new Point(x, 0)); pending.Enqueue(new Point(x, image.Height - 1)); }
        for (var y = 0; y < image.Height; y++) { pending.Enqueue(new Point(0, y)); pending.Enqueue(new Point(image.Width - 1, y)); }
        while (pending.TryDequeue(out var p))
        {
            if (p.X < 0 || p.Y < 0 || p.X >= image.Width || p.Y >= image.Height) continue;
            var index = p.Y * image.Width + p.X; if (seen[index]) continue; seen[index] = true;
            var color = image.GetPixel(p.X, p.Y);
            if (Math.Abs(color.R - background.R) + Math.Abs(color.G - background.G) + Math.Abs(color.B - background.B) > 90) continue;
            image.SetPixel(p.X, p.Y, Color.Transparent);
            pending.Enqueue(new Point(p.X - 1, p.Y)); pending.Enqueue(new Point(p.X + 1, p.Y)); pending.Enqueue(new Point(p.X, p.Y - 1)); pending.Enqueue(new Point(p.X, p.Y + 1));
        }
    }
    public static string Save(ImagePayload sticker)
    {
        ImageAttachments.Validate(sticker);
        if (!sticker.Info.IsSticker) throw new InvalidDataException("Сначала подготовьте стикер.");
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, sticker.Info.Sha256.ToLowerInvariant() + ".png");
        if (File.Exists(path)) return path;
        if (Files().Length >= 100) throw new IOException("В коллекции уже 100 стикеров. Удалите ненужные.");
        File.WriteAllBytes(path, sticker.Data); return path;
    }
    public static ImagePayload Load(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Неверный путь стикера.");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Стикер слишком большой.");
        var payload = ImageAttachments.Create(File.ReadAllBytes(path), "Стикер.png");
        payload = payload with { Info = payload.Info with { IsSticker = true } }; ImageAttachments.Validate(payload); return payload;
    }
}
