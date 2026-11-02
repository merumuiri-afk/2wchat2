using System.Buffers.Binary;
using NAudio.Wave;
using TwoWChat.Core;

namespace TwoWChat.Audio;

public sealed record AudioDevice(int Number, string Name)
{
    public override string ToString() => Name;
}

public sealed class AudioEngine : IDisposable
{
    private WaveInEvent? _capture;
    private WaveOutEvent? _output;
    private VoiceEncoder? _encoder;
    private readonly object _captureGate = new();
    private readonly short[] _frame = new short[Protocol.FrameSamples];
    private readonly MicrophoneFilter _filter = new();
    private int _fill;
    private uint _sequence;
    private volatile bool _disposed;
    private readonly bool _monitor;
    private readonly Guid _monitorId = Guid.NewGuid();
    public VoiceMixer Mixer { get; }
    public volatile bool MicMuted, Deafened, PushToTalk, TalkHeld;
    public volatile bool GateEnabled;
    public int GateDb = -42;
    public float MicrophoneGain = 1;
    public float InputLevel => _filter.InputLevel;
    public bool Transmitting { get; private set; }
    public bool HasMicrophone => _capture != null;
    public bool HasOutput => _output != null;
    public string Codec => _encoder?.Implementation ?? "Opus";
    public event Action<uint, byte, byte[]>? Encoded;
    public event Action<string>? Error;

    public AudioEngine(int jitterMs = 60, bool monitor = false)
    { Mixer = new VoiceMixer(jitterMs); _monitor = monitor; }

    public static AudioDevice[] InputDevices()
    {
        var list = new List<AudioDevice> { new(-1, "По умолчанию Windows") };
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(new(i, WaveInEvent.GetCapabilities(i).ProductName));
        return list.ToArray();
    }
    public static AudioDevice[] OutputDevices()
    {
        var list = new List<AudioDevice> { new(-1, "По умолчанию Windows") };
        for (var i = 0; i < WaveOutEvent.DeviceCount; i++) list.Add(new(i, WaveOutEvent.GetCapabilities(i).ProductName));
        return list.ToArray();
    }

    // Independent failure handling: text chat continues if an audio device is absent.
    public void Start(int inputDevice, int outputDevice, int bitrate = 32000)
    {
        try
        {
            _output = new WaveOutEvent { DeviceNumber = outputDevice, DesiredLatency = 60, NumberOfBuffers = 3 };
            _output.PlaybackStopped += (_, e) => { if (e.Exception != null && !_disposed) Error?.Invoke("Устройство вывода отключено: " + e.Exception.Message); };
            _output.Init(Mixer);
            _output.Play();
        }
        catch (Exception e) when (e is NAudio.MmException or InvalidOperationException or ArgumentException)
        { _output?.Dispose(); _output = null; Error?.Invoke("Не удалось открыть динамики/наушники: " + e.Message); }
        try
        {
            _encoder = new VoiceEncoder(bitrate);
            _capture = new WaveInEvent
            {
                DeviceNumber = inputDevice, WaveFormat = new WaveFormat(Protocol.SampleRate, 16, 1),
                BufferMilliseconds = 20, NumberOfBuffers = 3
            };
            _capture.DataAvailable += OnCaptured;
            _capture.RecordingStopped += (_, e) => { if (e.Exception != null && !_disposed) Error?.Invoke("Микрофон отключён: " + e.Exception.Message); };
            _capture.StartRecording();
        }
        catch (Exception e) when (e is NAudio.MmException or InvalidOperationException or ArgumentException)
        { _capture?.Dispose(); _capture = null; Error?.Invoke("Не удалось открыть микрофон: " + e.Message + " Проверьте доступ к микрофону в параметрах Windows."); }
    }

    private void OnCaptured(object? sender, WaveInEventArgs e)
    {
        lock (_captureGate)
        {
            if (_disposed) return;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                _frame[_fill++] = BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i, 2));
                if (_fill < _frame.Length) continue;
                _fill = 0;
                _sequence++;
                var audible = _filter.Process(_frame, MicrophoneGain, GateEnabled, GateDb);
                Transmitting = !MicMuted && !Deafened && (!PushToTalk || TalkHeld) && audible;
                if (!Transmitting) continue;
                var level = (byte)Math.Clamp(_filter.InputLevel * 500, 0, 255);
                var opus = _encoder!.Encode(_frame);
                if (_monitor) Mixer.Add(new VoiceFrame(_monitorId, _sequence, level, opus));
                else Encoded?.Invoke(_sequence, level, opus);
            }
        }
    }

    public void AddVoice(VoiceFrame frame) { if (!_disposed && !Deafened) Mixer.Add(frame); }
    public void Dispose()
    {
        _disposed = true;
        _capture?.StopRecording();
        _capture?.Dispose();
        lock (_captureGate) { _encoder?.Dispose(); }
        _output?.Stop();
        _output?.Dispose();
        Mixer.Dispose();
    }
}
