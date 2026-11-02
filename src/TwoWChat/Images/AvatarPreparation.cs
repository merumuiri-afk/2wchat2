using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TwoWChat.Core;

namespace TwoWChat.Images;

public static class AvatarPreparation
{
    public static byte[] FromFile(string path)
    {
        var payload = ImagePreparation.FromFile(path);
        using var stream = new MemoryStream(payload.Data);
        using var source = Image.FromStream(stream);
        return FromImage(source);
    }
    public static byte[] FromImage(Image source)
    {
        using var bitmap = new Bitmap(AvatarData.Size, AvatarData.Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var side = Math.Min(source.Width, source.Height);
            g.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height), new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        }
        using var bytes = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
        bitmap.Save(bytes, codec, parameters);
        var data = bytes.ToArray();
        if (!AvatarData.IsValid(data)) throw new InvalidDataException("Не удалось подготовить аватарку.");
        return data;
    }
    public static Bitmap Render(byte[]? data, string name, int size = 32)
    {
        var result = new Bitmap(size, size);
        using var g = Graphics.FromImage(result);
        if (data is { Length: > 0 } && AvatarData.IsValid(data))
        {
            try
            {
                using var memory = new MemoryStream(data); using var source = Image.FromStream(memory, true, true);
                if (source.Width != AvatarData.Size || source.Height != AvatarData.Size) throw new ArgumentException();
                g.DrawImage(source, 0, 0, size, size); return result;
            }
            catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException) { }
        }
        g.Clear(Color.FromArgb(10, 36, 106));
        using var font = new Font("Tahoma", size * .45f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var text = string.IsNullOrWhiteSpace(name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(name.Trim());
        g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size), format);
        return result;
    }
}
