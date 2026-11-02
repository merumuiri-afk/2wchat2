using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TwoWChat.Core;

// A separate random session key and nonce namespace for each direction.
// The authenticated header binds a datagram to the TLS-authenticated session.
public sealed class UdpCipher : IDisposable
{
    public const int HeaderSize = 30;
    public const int TagSize = 16;
    public const int MaxPacket = 1400;
    private readonly Guid _token;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly uint _sendDomain;
    private readonly uint _receiveDomain;
    private readonly object _sendLock = new();
    private readonly object _receiveLock = new();
    private ulong _counter;
    private ulong _highest;
    private ulong _seen;

    public UdpCipher(Guid token, byte[] key, bool client)
    {
        if (key.Length != 32) throw new ArgumentException("A 256-bit key is required.", nameof(key));
        _token = token;
        _send = new AesGcm(key, TagSize);
        _receive = new AesGcm(key, TagSize);
        _sendDomain = client ? 0x434C4E54u : 0x53525652u;
        _receiveDomain = client ? 0x53525652u : 0x434C4E54u;
    }

    public byte[] Seal(byte kind, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPacket - HeaderSize - TagSize) throw new ArgumentOutOfRangeException(nameof(plaintext));
        lock (_sendLock)
        {
            var packet = new byte[HeaderSize + plaintext.Length + TagSize];
            "2WVC"u8.CopyTo(packet);
            packet[4] = Protocol.Version;
            packet[5] = kind;
            _token.TryWriteBytes(packet.AsSpan(6, 16));
            var counter = checked(++_counter);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(22, 8), counter);
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(nonce, _sendDomain);
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
            _send.Encrypt(nonce, plaintext, packet.AsSpan(HeaderSize, plaintext.Length), packet.AsSpan(packet.Length - TagSize), packet.AsSpan(0, HeaderSize));
            return packet;
        }
    }

    public static bool TryGetToken(ReadOnlySpan<byte> data, out Guid token)
    {
        token = Guid.Empty;
        if (data.Length < HeaderSize + TagSize || data.Length > MaxPacket || !data[..4].SequenceEqual("2WVC"u8) || data[4] != Protocol.Version) return false;
        token = new Guid(data.Slice(6, 16));
        return true;
    }

    public bool TryOpen(ReadOnlySpan<byte> packet, out byte kind, out byte[] payload)
    {
        kind = 0;
        payload = [];
        if (!TryGetToken(packet, out var token) || token != _token) return false;
        lock (_receiveLock)
        {
            var counter = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(22, 8));
            if (counter == 0 || (_highest >= counter && (_highest - counter >= 64 || (_seen & (1UL << (int)(_highest - counter))) != 0))) return false;
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(nonce, _receiveDomain);
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
            var plain = new byte[packet.Length - HeaderSize - TagSize];
            try
            {
                _receive.Decrypt(nonce, packet.Slice(HeaderSize, plain.Length), packet[^TagSize..], plain, packet[..HeaderSize]);
            }
            catch (CryptographicException) { return false; }
            if (counter > _highest)
            {
                var shift = counter - _highest;
                _seen = shift >= 64 ? 1 : (_seen << (int)shift) | 1;
                _highest = counter;
            }
            else _seen |= 1UL << (int)(_highest - counter);
            kind = packet[5];
            payload = plain;
            return true;
        }
    }

    public static byte[] PackVoice(VoiceFrame frame)
    {
        if (frame.Opus.Length is < 1 or > 1275) throw new ArgumentOutOfRangeException(nameof(frame));
        var data = new byte[21 + frame.Opus.Length];
        frame.Speaker.TryWriteBytes(data.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), frame.Sequence);
        data[20] = frame.Level;
        frame.Opus.CopyTo(data, 21);
        return data;
    }

    public static VoiceFrame? UnpackVoice(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 22 or > 1296) return null;
        return new VoiceFrame(new Guid(data[..16]), BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4)), data[20], data[21..].ToArray());
    }

    public void Dispose()
    {
        lock (_sendLock) _send.Dispose();
        lock (_receiveLock) _receive.Dispose();
    }
}
