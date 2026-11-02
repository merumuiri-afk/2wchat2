using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TwoWChat.Core;

namespace TwoWChat.Images;

public static class ImagePreparation
{
    public const int MaxSourceBytes = 32 * 1024 * 1024;
    public static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp";
    public static ImagePayload FromFile(string path)
    {
        if (!IsImageFile(path)) throw new InvalidDataException("Выберите PNG, JPEG, GIF или BMP.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaxSourceBytes) throw new InvalidDataException("Исходный файл должен быть не больше 32 МБ.");
        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            if (file.Length > Protocol.MaxImageBytes) throw new InvalidDataException("GIF должен быть не больше 8 МБ.");
            var data = new byte[(int)file.Length]; file.ReadExactly(data);
            var payload = ImageAttachments.Create(data, Path.GetFileName(path));
            if (payload.Info.ContentType != "image/gif") throw new InvalidDataException("Некорректный GIF.");
            using var verified = OpenAnimation(payload);
            (verified.Tag as Stream)?.Dispose();
            return payload;
        }
        using var image = Image.FromStream(file, true, false);
        return Prepare(image, Path.GetFileName(path), Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg");
    }
    public static ImagePayload FromBitmap(Bitmap bitmap, string name = "Изображение.png") => Prepare(bitmap, name, false);

    private static ImagePayload Prepare(Image source, string name, bool jpeg)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Width > 16384 || source.Height > 16384 || (long)source.Width * source.Height > 320000000)
            throw new InvalidDataException("Исходное изображение слишком большое (максимум 40 мегапикселей).");
        // Apply photo orientation before stripping EXIF and other source metadata.
        try
        {
            var bytes = source.GetPropertyItem(0x112)?.Value;
            var orientation = bytes?.Length >= 2 ? BitConverter.ToUInt16(bytes, 0) : 1;
            var rotate = orientation switch
            {
                2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
            };
            if (rotate != RotateFlipType.RotateNoneFlipNone) source.RotateFlip(rotate);
        }
        catch (ArgumentException) { }
        var scale = Math.Min(1d, Protocol.MaxImageDimension / (double)Math.Max(source.Width, source.Height));
        for (var attempt = 0; attempt < 10; attempt++, scale *= .75)
        {
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(normalized))
            {
                graphics.Clear(jpeg ? Color.White : Color.Transparent);
                graphics.CompositingMode = jpeg ? CompositingMode.SourceOver : CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            using var memory = new MemoryStream();
            if (jpeg)
            {
                var codec = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                normalized.Save(memory, codec, parameters);
            }
            else normalized.Save(memory, ImageFormat.Png);
            if (memory.Length <= Protocol.MaxImageBytes) return ImageAttachments.Create(memory.ToArray(), name);
        }
        throw new InvalidDataException("Не удалось подготовить изображение размером до 8 МБ.");
    }

    public static Bitmap Decode(ImagePayload payload)
    {
        ImageAttachments.Validate(payload);
        using var stream = new MemoryStream(payload.Data, false);
        using var image = Image.FromStream(stream, true, true);
        if (image.Width != payload.Info.Width || image.Height != payload.Info.Height) throw new InvalidDataException("Неверные размеры изображения.");
        return new Bitmap(image);
    }

    public static Bitmap Thumbnail(ImagePayload payload)
    {
        using var decoded = Decode(payload);
        var scale = Math.Min(1d, Math.Min(320d / decoded.Width, 220d / decoded.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(decoded, new Rectangle(Point.Empty, bitmap.Size)); return bitmap;
    }
    public static Image OpenAnimation(ImagePayload payload)
    {
        ImageAttachments.Validate(payload);
        var stream = new MemoryStream(payload.Data, false);
        try
        {
            var image = Image.FromStream(stream, true, true);
            var frames = image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
            if (frames > 300 || (long)image.Width * image.Height * frames > 32000000)
            { image.Dispose(); throw new InvalidDataException("GIF слишком большой: максимум 300 кадров и 32 млн пикселей суммарно по кадрам."); }
            image.Tag = stream; return image;
        }
        catch { stream.Dispose(); throw; }
    }
    public static string Description(ImageInfo image) => $"{image.FileName}  •  {image.Width}×{image.Height}  •  {image.Length / 1048576d:F2} МБ";
}
