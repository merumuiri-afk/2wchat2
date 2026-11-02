using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TwoWChat.Core;

public sealed class VoiceEncoder : IDisposable
{
    private readonly OpusEncoderHandle _encoder;
    private readonly byte[] _encoded = new byte[1275];
    public string Implementation => Marshal.PtrToStringUTF8(OpusNative.opus_get_version_string()) ?? "libopus";
    public VoiceEncoder(int bitrate = 32000)
    {
        _encoder = OpusNative.opus_encoder_create(Protocol.SampleRate, 1, 2048, out var error);
        if (error != 0 || _encoder.IsInvalid) throw new InvalidOperationException("Не удалось создать кодировщик Opus: " + error);
        try
        {
            foreach (var (request, value) in new[] { (4002, bitrate), (4010, 5), (4006, 1), (4012, 1), (4014, 5), (4016, 1) })
                if (OpusNative.opus_encoder_ctl(_encoder, request, value) != 0) throw new InvalidOperationException("Не удалось настроить Opus.");
        }
        catch { _encoder.Dispose(); throw; }
    }
    public byte[] Encode(short[] samples)
    {
        if (samples.Length != Protocol.FrameSamples) throw new ArgumentException("A 20ms frame is required.");
        var length = OpusNative.opus_encode(_encoder, samples, samples.Length, _encoded, _encoded.Length);
        if (length < 1) throw new InvalidDataException("Ошибка кодирования Opus: " + length);
        return _encoded.AsSpan(0, length).ToArray();
    }
    public void Dispose() => _encoder.Dispose();
}

public sealed class VoiceDecoder : IDisposable
{
    private readonly OpusDecoderHandle _decoder;
    public VoiceDecoder()
    {
        _decoder = OpusNative.opus_decoder_create(Protocol.SampleRate, 1, out var error);
        if (error != 0 || _decoder.IsInvalid) throw new InvalidOperationException("Не удалось создать декодировщик Opus: " + error);
    }
    public int Decode(byte[]? packet, short[] output, bool fec = false)
    {
        if (output.Length < Protocol.FrameSamples) throw new ArgumentException("Output buffer is too short.");
        var count = OpusNative.opus_decode(_decoder, packet, packet?.Length ?? 0, output, Protocol.FrameSamples, fec ? 1 : 0);
        if (count < 0) throw new InvalidDataException("Ошибка декодирования Opus: " + count);
        return count;
    }
    public void Dispose() => _decoder.Dispose();
}

internal sealed class OpusEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public OpusEncoderHandle() : base(true) { }
    protected override bool ReleaseHandle() { OpusNative.opus_encoder_destroy(handle); return true; }
}
internal sealed class OpusDecoderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public OpusDecoderHandle() : base(true) { }
    protected override bool ReleaseHandle() { OpusNative.opus_decoder_destroy(handle); return true; }
}
internal static class OpusNative
{
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern OpusEncoderHandle opus_encoder_create(int rate, int channels, int application, out int error);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern OpusDecoderHandle opus_decoder_create(int rate, int channels, out int error);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern int opus_encode(OpusEncoderHandle encoder, [In] short[] pcm, int frameSize, [Out] byte[] data, int maxLength);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern int opus_decode(OpusDecoderHandle decoder, [In] byte[]? data, int length, [Out] short[] pcm, int frameSize, int fec);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern int opus_encoder_ctl(OpusEncoderHandle encoder, int request, int value);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern void opus_encoder_destroy(IntPtr encoder);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern void opus_decoder_destroy(IntPtr decoder);
    [DllImport("opus", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr opus_get_version_string();
}

// A small reorder buffer. It is pulled by the audio device's playback clock,
// never by a periodic UI timer. No accumulating playback queue after stalls.
public sealed class VoiceJitterBuffer : IDisposable
{
    private readonly SortedDictionary<long, byte[]> _frames = [];
    private readonly VoiceDecoder _decoder = new();
    private readonly object _gate = new();
    private readonly int _targetFrames;
    private long _expected, _lastArrival;
    private bool _started;
    private int _missing;
    private int _disposed;
    public long LostFrames { get; private set; }
    public long ReceivedFrames { get; private set; }
    public int BufferedFrames { get { lock (_gate) return _frames.Count; } }
    public VoiceJitterBuffer(int delayMs = 60) => _targetFrames = Math.Clamp(delayMs / 20, 2, 10);

    public void Add(uint sequence, byte[] opus)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            var now = Environment.TickCount64;
            // 32-bit sample counter wrap is expanded near the playback position.
            var extended = (long)sequence;
            if (_started)
            {
                extended = (_expected & ~0xffffffffL) | sequence;
                if (extended - _expected > int.MaxValue) extended -= 1L << 32;
                else if (_expected - extended > int.MaxValue) extended += 1L << 32;
            }
            if (now - _lastArrival > 300 || (_started && extended - _expected > 50))
            { _frames.Clear(); _started = false; _missing = 0; }
            _lastArrival = now;
            if (_started && extended < _expected) return;
            if (_frames.TryAdd(extended, opus)) ReceivedFrames++;
            if (_frames.Count > 12)
            {
                while (_frames.Count > _targetFrames) _frames.Remove(_frames.Keys.First());
                _expected = _frames.Keys.First();
            }
        }
    }

    public void ReadFrame(short[] output)
    {
        Array.Clear(output);
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (!_started)
            {
                if (_frames.Count == 0 || (_frames.Count < _targetFrames && Environment.TickCount64 - _lastArrival < _targetFrames * 20)) return;
                _expected = _frames.Keys.First();
                _started = true;
            }
            try
            {
                if (_frames.Remove(_expected, out var packet))
                {
                    var count = _decoder.Decode(packet, output);
                    if (count < output.Length) Array.Clear(output, count, output.Length - count);
                    _missing = 0;
                }
                else
                {
                    _missing++;
                    if (_missing <= 3)
                    {
                        // FEC in the next packet can reconstruct a missing previous frame.
                        if (_frames.TryGetValue(_expected + 1, out var next)) _decoder.Decode(next, output, true);
                        else _decoder.Decode(null, output);
                        if (_frames.Count > 0) LostFrames++;
                    }
                    else { _started = false; return; }
                }
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { Array.Clear(output); LostFrames++; }
            _expected++;
        }
    }

    public void Dispose() { lock (_gate) { if (_disposed++ == 0) _decoder.Dispose(); _frames.Clear(); } }
}

// A light high-pass filter, adjustable gate with hysteresis and a release ramp.
// This suppresses quiet background sound; it is deliberately not advertised as AEC.
public sealed class MicrophoneFilter
{
    private float _previousIn, _previousOut, _gateGain;
    private int _hold;
    public float InputLevel { get; private set; }
    public bool GateOpen { get; private set; }
    public bool Process(short[] frame, float gain, bool gateEnabled, int thresholdDb)
    {
        double square = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            var input = frame[i] / 32768f;
            var filtered = .990f * (_previousOut + input - _previousIn);
            _previousIn = input;
            _previousOut = filtered;
            var sample = Math.Clamp(filtered * gain, -1f, 1f);
            frame[i] = (short)(sample * 32767);
            square += sample * sample;
        }
        InputLevel = (float)Math.Sqrt(square / frame.Length);
        var threshold = MathF.Pow(10, thresholdDb / 20f);
        if (!gateEnabled || InputLevel >= threshold) _hold = 12;
        else if (InputLevel < threshold * .65f) _hold = Math.Max(0, _hold - 1);
        GateOpen = !gateEnabled || _hold > 0;
        var target = GateOpen ? 1f : 0f;
        for (var i = 0; i < frame.Length; i++)
        {
            _gateGain += (target - _gateGain) * (GateOpen ? .02f : .002f);
            frame[i] = (short)(frame[i] * _gateGain);
        }
        return GateOpen || _gateGain > .01f;
    }
}
