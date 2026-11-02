using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TwoWChat.ScreenSharing;

public sealed record ScreenCaptureOptions(Rectangle Bounds, int FramesPerSecond = 10, int JpegQuality = 60, bool IncludeCursor = true);

// Desktop pixels are read only after Start. A supplied factory completely
// replaces desktop capture, allowing deterministic and private synthetic QA.
public sealed class ScreenCaptureService : IDisposable, IAsyncDisposable
{
    public const int MaxFrameBytes = 384 * 1024;
    public const int MaxWidth = 1280;
    public const int MaxHeight = 720;
    public const int TargetBytesPerSecond = 500000;
    private readonly ScreenCaptureOptions _options;
    private readonly Func<Bitmap>? _frameSource;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;
    public event Action<byte[]>? FrameCaptured;
    public event Action<string>? Error;
    public long FramesCaptured { get; private set; }
    public long BytesEncoded { get; private set; }
    public bool Running => _loop is { IsCompleted: false } && !_stop.IsCancellationRequested;

    public ScreenCaptureService(ScreenCaptureOptions options, Func<Bitmap>? frameSource = null)
    {
        if (options.Bounds.Width <= 0 || options.Bounds.Height <= 0 || options.Bounds.Width > 16384 || options.Bounds.Height > 16384 ||
            (long)options.Bounds.Width * options.Bounds.Height > 40000000)
            throw new ArgumentException("Размер выбранного экрана не поддерживается.", nameof(options));
        if (options.FramesPerSecond is not (5 or 10)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.JpegQuality is < 30 or > 85) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options; _frameSource = frameSource;
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop != null) throw new InvalidOperationException("Захват уже запускался.");
        _loop = Task.Run(CaptureLoopAsync);
    }
    private async Task CaptureLoopAsync()
    {
        try
        {
            using var desktop = _frameSource == null ? new Bitmap(_options.Bounds.Width, _options.Bounds.Height, PixelFormat.Format24bppRgb) : null;
            var interval = TimeSpan.FromSeconds(1d / _options.FramesPerSecond);
            var maxBytes = Math.Min(MaxFrameBytes, TargetBytesPerSecond / _options.FramesPerSecond);
            while (!_stop.IsCancellationRequested)
            {
                var watch = Stopwatch.StartNew();
                byte[] jpeg;
                if (_frameSource != null)
                {
                    using var frame = _frameSource();
                    jpeg = EncodeFrame(frame, _options.JpegQuality, maxBytes);
                }
                else
                {
                    CopyDesktop(desktop!, _options.Bounds, _options.IncludeCursor);
                    jpeg = EncodeFrame(desktop!, _options.JpegQuality, maxBytes);
                }
                _stop.Token.ThrowIfCancellationRequested();
                FramesCaptured++; BytesEncoded += jpeg.Length;
                FrameCaptured?.Invoke(jpeg);
                var remaining = interval - watch.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _stop.Token);
                else await Task.Yield(); // Never queue missed capture ticks.
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is Win32Exception or ExternalException or ArgumentException or IOException or InvalidOperationException)
        {
            if (!_stop.IsCancellationRequested) Error?.Invoke("Захват экрана остановлен: " + e.Message);
        }
    }
    public static Bitmap CapturePreview(Rectangle bounds, bool includeCursor = true)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 40000000) throw new ArgumentException("Недопустимый экран.");
        using var raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        CopyDesktop(raw, bounds, includeCursor);
        using var memory = new MemoryStream(EncodeFrame(raw));
        using var image = Image.FromStream(memory, false, true);
        return new Bitmap(image);
    }
    private static void CopyDesktop(Bitmap frame, Rectangle bounds, bool includeCursor)
    {
        using var graphics = Graphics.FromImage(frame);
        // CopyFromScreen validates exact enum values, so combining CaptureBlt
        // with SourceCopy is rejected by System.Drawing before reaching GDI.
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        if (!includeCursor) return;
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || cursor.Flags != 1 || !bounds.Contains(cursor.Position)) return;
        if (!GetIconInfo(cursor.Cursor, out var icon)) return;
        try
        {
            var hdc = graphics.GetHdc();
            try { DrawIconEx(hdc, cursor.Position.X - bounds.X - (int)icon.HotspotX, cursor.Position.Y - bounds.Y - (int)icon.HotspotY, cursor.Cursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { graphics.ReleaseHdc(hdc); }
        }
        finally { if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); }
    }

    public static byte[] EncodeFrame(Bitmap source, int quality = 60, int maxBytes = MaxFrameBytes)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Width > 16384 || source.Height > 16384 || (long)source.Width * source.Height > 40000000)
            throw new InvalidDataException("Недопустимый размер кадра.");
        if (maxBytes is < 4096 or > MaxFrameBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var scale = Math.Min(1d, Math.Min(MaxWidth / (double)source.Width, MaxHeight / (double)source.Height));
        var codec = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var initialQuality = Math.Clamp(quality, 30, 85);
        for (var attempt = 0; attempt < 14; attempt++)
        {
            var width = Math.Max(1, (int)(source.Width * scale)); var height = Math.Max(1, (int)(source.Height * scale));
            using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            using var memory = new MemoryStream(); using var parameters = new EncoderParameters(1);
            var currentQuality = Math.Max(30, initialQuality - (attempt % 3) * 12);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)currentQuality);
            resized.Save(memory, codec, parameters);
            if (memory.Length <= maxBytes) return memory.ToArray();
            if (attempt % 3 == 2) scale *= .75;
        }
        throw new InvalidDataException("Кадр не удалось сжать до допустимого размера.");
    }
    public async Task StopAsync()
    {
        if (_disposed) return;
        _stop.Cancel(); if (_loop != null) await _loop.ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (_disposed) return; StopAsync().GetAwaiter().GetResult(); _disposed = true; _stop.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; await StopAsync().ConfigureAwait(false); _disposed = true; _stop.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size; public int Flags; public IntPtr Cursor; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint HotspotX; public uint HotspotY; public IntPtr Mask; public IntPtr Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
}
