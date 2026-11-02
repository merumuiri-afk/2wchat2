using System.Buffers.Binary;

namespace TwoWChat.Core;

public sealed record ScreenShareInfo(Guid OwnerId, string Name, string Title, int Width, int Height);
public sealed record ScreenFrame(Guid OwnerId, byte[] Jpeg);

public static class ScreenWire
{
    public const int MaxFrameBytes = 384 * 1024;
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    public static void ValidateFrame(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length is < 24 or > MaxFrameBytes) throw new InvalidDataException("Недопустимый размер кадра демонстрации.");
        var (type, width, height) = ImageAttachments.Inspect(jpeg);
        if (type != "image/jpeg" || width > MaxWidth || height > MaxHeight)
            throw new InvalidDataException("Кадр демонстрации должен быть JPEG не больше 1920×1080.");
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), ct) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 24 or > MaxFrameBytes) throw new InvalidDataException("Недопустимый размер кадра демонстрации.");
        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, ct);
        ValidateFrame(frame);
        return frame;
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] frame, CancellationToken ct)
    {
        if (frame.Length is < 24 or > MaxFrameBytes) throw new InvalidDataException("Недопустимый размер кадра демонстрации.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(frame, ct);
    }
}
