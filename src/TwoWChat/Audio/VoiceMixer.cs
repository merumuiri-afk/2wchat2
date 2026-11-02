using System.Buffers;
using System.Buffers.Binary;
using NAudio.Wave;
using TwoWChat.Core;

namespace TwoWChat.Audio;

public sealed class VoiceMixer(int jitterMs = 60) : IWaveProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Speaker> _speakers = [];
    private bool _disposed;
    public WaveFormat WaveFormat { get; } = new(Protocol.SampleRate, 16, 1);
    public volatile bool Deafened;
    public float Volume = .8f;
    public int ActiveStreams { get { lock (_gate) return _speakers.Count; } }
    public long LostFrames { get { lock (_gate) return _speakers.Values.Sum(s => s.Jitter.LostFrames); } }

    public void Add(VoiceFrame frame)
    {
        lock (_gate)
        {
            if (_disposed || Deafened) return;
            if (!_speakers.TryGetValue(frame.Speaker, out var speaker))
            {
                if (_speakers.Count >= Protocol.MaxPeople) return;
                speaker = new Speaker(jitterMs);
                _speakers.Add(frame.Speaker, speaker);
            }
            speaker.LastVoice = Environment.TickCount64;
            speaker.Level = frame.Level;
            if (speaker.Volume > 0) speaker.Jitter.Add(frame.Sequence, frame.Opus);
        }
    }

    public bool IsSpeaking(Guid id)
    { lock (_gate) return _speakers.TryGetValue(id, out var s) && s.Level > 1 && Environment.TickCount64 - s.LastVoice < 250 && s.Volume > 0; }

    public void SetUserVolume(Guid id, float volume)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_speakers.TryGetValue(id, out var s)) _speakers[id] = s = new Speaker(jitterMs);
            s.Volume = Math.Clamp(volume, 0, 2);
        }
    }
    public void SyncMembers(IEnumerable<Guid> ids)
    {
        var keep = ids.ToHashSet();
        lock (_gate)
            foreach (var id in _speakers.Keys.Where(id => !keep.Contains(id)).ToArray())
            { _speakers[id].Dispose(); _speakers.Remove(id); }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (_gate)
        {
            if (_disposed) return count;
            var samples = count / 2;
            var mix = ArrayPool<int>.Shared.Rent(samples);
            Array.Clear(mix, 0, samples);
            try
            {
                foreach (var speaker in _speakers.Values)
                {
                    // Drain even when deafened to avoid replaying old voice on unmute.
                    for (var i = 0; i < samples; i++)
                    {
                        if (speaker.Position >= Protocol.FrameSamples)
                        { speaker.Jitter.ReadFrame(speaker.Frame); speaker.Position = 0; }
                        mix[i] += (int)(speaker.Frame[speaker.Position++] * speaker.Volume);
                    }
                }
                if (!Deafened)
                    for (var i = 0; i < samples; i++)
                        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + i * 2, 2), (short)Math.Clamp(mix[i] * Volume, short.MinValue, short.MaxValue));
            }
            finally { ArrayPool<int>.Shared.Return(mix); }
        }
        return count;
    }

    public void Dispose()
    { lock (_gate) { _disposed = true; foreach (var s in _speakers.Values) s.Dispose(); _speakers.Clear(); } }

    private sealed class Speaker(int jitter) : IDisposable
    {
        public readonly VoiceJitterBuffer Jitter = new(jitter);
        public readonly short[] Frame = new short[Protocol.FrameSamples];
        public int Position = Protocol.FrameSamples;
        public float Volume = 1;
        public long LastVoice;
        public byte Level;
        public void Dispose() => Jitter.Dispose();
    }
}
