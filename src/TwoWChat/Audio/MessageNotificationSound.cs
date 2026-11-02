using NAudio.Wave;

namespace TwoWChat.Audio;

public sealed class MessageNotificationSound : IDisposable
{
    private static readonly Lazy<byte[]> WaveData = new(() =>
    {
        using var stream = typeof(MessageNotificationSound).Assembly.GetManifestResourceStream("TwoWChat.Assets.MessageNotification.wav")
            ?? throw new InvalidDataException("Встроенный звук уведомления не найден.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    });
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private volatile bool _disposed;
    private string? _lastError;
    public int PlayCount { get; private set; }
    public float LastVolume { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public event Action<string>? Error;
    public static WaveFileReader OpenEmbeddedWave() => new(new MemoryStream(WaveData.Value, false));

    // Start asynchronously, coalescing messages received while this clip plays.
    // Each player is tied to the selected output device; no temporary audio files.
    public bool TryPlay(int deviceNumber, float volume)
    {
        if (_disposed || volume <= 0 || IsPlaying) return false;
        try
        {
            Stop();
            _reader = OpenEmbeddedWave();
            var provider = new VolumeWaveProvider16(_reader) { Volume = Math.Clamp(volume, 0, 1.5f) };
            _output = new WaveOutEvent { DeviceNumber = deviceNumber, DesiredLatency = 100, NumberOfBuffers = 2 };
            _output.PlaybackStopped += (_, args) => { if (args.Exception != null && !_disposed) ReportError(args.Exception); };
            _output.Init(provider); _output.Play(); PlayCount++; LastVolume = provider.Volume;
            _lastError = null;
            return true;
        }
        catch (Exception e) when (e is NAudio.MmException or IOException or InvalidOperationException or ArgumentException)
        { Stop(); ReportError(e); return false; }
    }

    private void ReportError(Exception error)
    {
        if (_lastError == error.Message) return;
        _lastError = error.Message;
        Error?.Invoke("Не удалось воспроизвести уведомление: " + error.Message);
    }
    public void Stop()
    {
        var output = _output; _output = null;
        output?.Stop(); output?.Dispose();
        _reader?.Dispose(); _reader = null;
    }
    public void Dispose() { _disposed = true; Stop(); }
}
