using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TwoWChat.Core;

public sealed record ImageInfo(Guid Id, string FileName, string ContentType, int Length, int Width, int Height, string Sha256);
public sealed record ImagePayload(ImageInfo Info, byte[] Data);

public static class ImageAttachments
{
    public static ImagePayload Create(byte[] data, string fileName)
    {
        var (type, width, height) = Inspect(data);
        var name = SafeFileName(fileName, type);
        return new ImagePayload(new ImageInfo(Guid.NewGuid(), name, type, data.Length, width, height, Convert.ToHexString(SHA256.HashData(data))), data);
    }

    public static string SafeFileName(string? name, string type)
    {
        name = Protocol.CleanName(Path.GetFileName(name ?? ""), 70);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Path.GetFileNameWithoutExtension(name).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Изображение";
        return name + (type == "image/png" ? ".png" : ".jpg");
    }

    public static void ValidateInfo(ImageInfo? info)
    {
        if (info == null || info.Id == Guid.Empty || info.Length is < 24 or > Protocol.MaxImageBytes ||
            info.Width is < 1 or > Protocol.MaxImageDimension || info.Height is < 1 or > Protocol.MaxImageDimension ||
            info.ContentType is not ("image/png" or "image/jpeg") ||
            string.IsNullOrWhiteSpace(info.Sha256) || info.Sha256.Length != 64 || !info.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Недопустимое изображение. Максимум — 8 МБ и 4096×4096 пикселей.");
    }

    public static void Validate(ImagePayload image)
    {
        ValidateInfo(image.Info);
        if (image.Data.Length != image.Info.Length) throw new InvalidDataException("Изображение передано не полностью.");
        var (type, width, height) = Inspect(image.Data);
        if (type != image.Info.ContentType || width != image.Info.Width || height != image.Info.Height ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(image.Data)), image.Info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Изображение повреждено при передаче.");
    }

    // Inspect dimensions before any pixel allocation in the Windows image decoder.
    public static (string Type, int Width, int Height) Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 24 or > Protocol.MaxImageBytes) throw new InvalidDataException("Изображение должно быть не больше 8 МБ.");
        if (data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4)) == 13 && data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Dimensions("image/png", BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4)));
        if (data[0] == 0xff && data[1] == 0xd8)
        {
            var offset = 2;
            while (offset + 3 < data.Length)
            {
                if (data[offset++] != 0xff) break;
                while (offset < data.Length && data[offset] == 0xff) offset++;
                if (offset >= data.Length) break;
                var marker = data[offset++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (offset + 2 > data.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                if (length < 2 || offset + length > data.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 8) break;
                    return Dimensions("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2)));
                }
                offset += length;
            }
        }
        throw new InvalidDataException("Поддерживаются изображения PNG и JPEG.");
    }
    private static (string, int, int) Dimensions(string type, int width, int height)
    {
        if (width is < 1 or > Protocol.MaxImageDimension || height is < 1 or > Protocol.MaxImageDimension)
            throw new InvalidDataException("Размер изображения превышает 4096×4096 пикселей.");
        return (type, width, height);
    }
}

internal sealed class ImageTransferBuffer
{
    public ImageInfo Info { get; }
    public byte[] Data { get; }
    public string Caption { get; }
    public int Received { get; private set; }
    public long LastActivity { get; private set; } = Environment.TickCount64;
    public ImageTransferBuffer(ImageInfo info, string caption = "")
    { ImageAttachments.ValidateInfo(info); Info = info; Data = new byte[info.Length]; Caption = caption; }
    public void Append(int offset, byte[]? chunk)
    {
        if (chunk == null || chunk.Length is < 1 or > Protocol.ImageChunkBytes || offset != Received || Received + chunk.Length > Data.Length)
            throw new InvalidDataException("Нарушен порядок частей изображения.");
        chunk.CopyTo(Data, Received); Received += chunk.Length; LastActivity = Environment.TickCount64;
    }
    public ImagePayload Complete()
    {
        if (Received != Data.Length) throw new InvalidDataException("Изображение передано не полностью.");
        var result = new ImagePayload(Info, Data); ImageAttachments.Validate(result); return result;
    }
}

internal sealed class ImageCache
{
    private readonly Dictionary<Guid, ImagePayload> _items = [];
    private readonly Queue<Guid> _order = new();
    private int _bytes;
    public ImagePayload? Get(Guid id) => _items.GetValueOrDefault(id);
    public void Add(ImagePayload image)
    {
        if (_items.ContainsKey(image.Info.Id)) return;
        _items[image.Info.Id] = image; _order.Enqueue(image.Info.Id); _bytes += image.Data.Length;
        while (_bytes > Protocol.ImageCacheBytes && _order.TryDequeue(out var id))
            if (_items.Remove(id, out var removed)) _bytes -= removed.Data.Length;
    }
    public void Clear() { _items.Clear(); _order.Clear(); _bytes = 0; }
}
