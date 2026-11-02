using NAudio.Wave;

if (args.Length != 2) throw new ArgumentException("Usage: AudioAssetBuilder input.mp3 output.wav");
using var reader = new Mp3FileReaderBase(args[0], format => new AcmMp3FrameDecompressor(format));
WaveFileWriter.CreateWaveFile(args[1], reader);
using var decoded = new WaveFileReader(args[1]);
Console.WriteLine($"Notification sound: {decoded.TotalTime.TotalSeconds:F3}s; {decoded.WaveFormat}; {decoded.Length} PCM bytes");
