using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwoWChat.Core;

public static class Protocol
{
    public const int Version = 1;
    public const int DefaultPort = 48500;
    public const int MaxPeople = 16;
    public const int MaxMessageBytes = 32768;
    public const int MaxTextLength = 2000;
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const int ImageChunkBytes = 12 * 1024;
    public const int ImageCacheBytes = 32 * 1024 * 1024;
    public const int MaxImageDimension = 4096;
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12
    };

    public static string CleanName(string? value, int max = 24)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (text.Length > max) text = text[..max];
        return text;
    }

    public static string CleanText(string? value)
    {
        var text = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        text = new string(text.Where(c => (c == '\n' || c == '\t' || !char.IsControl(c)) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (text.Length > MaxTextLength) throw new InvalidDataException("Сообщение слишком длинное (максимум 2000 символов).");
        return text;
    }

    public static async Task<WireMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var n = await stream.ReadAsync(header.AsMemory(0, 1), ct);
        if (n == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Недопустимый размер пакета.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<WireMessage>(data, Json) ?? throw new InvalidDataException("Пустой пакет.");
    }

    public static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (data.Length > MaxMessageBytes) throw new InvalidDataException("Пакет слишком большой.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
    }
}

public sealed record PeerInfo(Guid Id, string Name, bool MicMuted, bool Deafened);
public sealed record ChatEntry(Guid Id, Guid Sender, string Name, string Text, DateTimeOffset Time)
{
    public ImageInfo? Image { get; init; }
}
public sealed record VoiceFrame(Guid Speaker, uint Sequence, byte Level, byte[] Opus);

public sealed record WireMessage(string Type)
{
    public int Version { get; init; } = Protocol.Version;
    public string? Name { get; init; }
    public string? Password { get; init; }
    public string? Room { get; init; }
    public string? Text { get; init; }
    public Guid Id { get; init; }
    public Guid Token { get; init; }
    public string? Key { get; init; }
    public bool MicMuted { get; init; }
    public bool Deafened { get; init; }
    public long Stamp { get; init; }
    public PeerInfo[]? Members { get; init; }
    public ChatEntry[]? History { get; init; }
    public ChatEntry? Chat { get; init; }
    public bool SupportsImages { get; init; }
    public ImageInfo? Image { get; init; }
    public byte[]? Data { get; init; }
    public int Offset { get; init; }
    public bool SupportsScreenShare { get; init; }
    public string? ScreenKey { get; init; }
    public ScreenShareInfo? Screen { get; init; }
    public ScreenShareInfo[]? Screens { get; init; }
}

internal sealed class RateLimit
{
    private readonly double _rate, _burst;
    private double _tokens;
    private long _last = Environment.TickCount64;
    public RateLimit(double rate, double burst) { _rate = rate; _burst = burst; _tokens = burst; }
    public bool Take()
    {
        var now = Environment.TickCount64;
        _tokens = Math.Min(_burst, _tokens + (now - _last) * _rate / 1000d);
        _last = now;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
