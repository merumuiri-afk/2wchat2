using System.Buffers.Binary;

namespace TwoWChat.Core;

public static class AvatarData
{
    public const int Size = 96;
    public const int MaxBytes = 12 * 1024;
    public static bool IsValid(byte[] data)
    {
        if (data.Length == 0) return true; // Explicit removal.
        if (data.Length is < 16 or > MaxBytes || data[0] != 255 || data[1] != 216 || data[^2] != 255 || data[^1] != 217) return false;
        // Require a bounded baseline JPEG before any desktop decoder sees the bytes.
        var offset = 2;
        var frameFound = false;
        while (offset + 4 <= data.Length)
        {
            if (data[offset++] != 255) return false;
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length) return false;
            var marker = data[offset++];
            if (marker == 0xda) return frameFound;
            if (marker == 0xd9) return false;
            if (offset + 2 > data.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            if (length < 2 || offset + length > data.Length) return false;
            if (marker == 0xc0)
            {
                if (frameFound || !(length >= 8 && data[offset + 2] == 8 &&
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3, 2)) == Size &&
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5, 2)) == Size)) return false;
                frameFound = true;
            }
            if (marker is >= 0xc1 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc)) return false;
            offset += length;
        }
        return false;
    }
}
