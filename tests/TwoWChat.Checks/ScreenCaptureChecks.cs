using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TwoWChat.Core;
using TwoWChat.ScreenSharing;

internal static partial class Program
{
    // Synthetic bitmaps only: no test in this file reads the real desktop.
    private static async Task CheckScreenCaptureAsync()
    {
        using (var large = new Bitmap(1920, 1080))
        {
            using (var graphics = Graphics.FromImage(large)) { graphics.Clear(Color.CornflowerBlue); graphics.FillRectangle(Brushes.Gold, 100, 100, 800, 500); }
            var jpeg = ScreenCaptureService.EncodeFrame(large);
            var (type, width, height) = ImageAttachments.Inspect(jpeg);
            Check(type == "image/jpeg" && width == 1280 && height == 720 && jpeg.Length <= ScreenCaptureService.MaxFrameBytes,
                "Screen encoder resizes a synthetic full-HD frame into the bounded 720p JPEG format");
            using var bytes = new MemoryStream(jpeg, false); using var decoded = Image.FromStream(bytes, false, true);
            Check(decoded.Width == width && decoded.Height == height, "Encoded screen frame decodes with the advertised dimensions");
        }
        using (var dense = new Bitmap(1024, 768, PixelFormat.Format32bppArgb))
        {
            var pixels = new byte[dense.Width * dense.Height * 4]; new Random(713).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var locked = dense.LockBits(new Rectangle(Point.Empty, dense.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length); } finally { dense.UnlockBits(locked); }
            var budget = ScreenCaptureService.TargetBytesPerSecond / 10;
            var jpeg = ScreenCaptureService.EncodeFrame(dense, 65, budget);
            var (type, width, height) = ImageAttachments.Inspect(jpeg);
            Check(type == "image/jpeg" && jpeg.Length <= budget && width <= 1280 && height <= 720,
                "Detailed screen frame adapts quality or resolution to the per-frame bandwidth budget");
        }
        var sourceCalls = 0;
        var frames = new ConcurrentQueue<byte[]>(); var errors = new ConcurrentQueue<string>();
        var options = new ScreenCaptureOptions(new Rectangle(0, 0, 1920, 1080), 5, 60, true);
        await using (var capture = new ScreenCaptureService(options, () =>
        {
            var number = Interlocked.Increment(ref sourceCalls);
            var bitmap = new Bitmap(960, 540);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(number * 41 % 255, number * 67 % 255, number * 97 % 255));
            graphics.FillRectangle(Brushes.White, number * 8, 40, 130, 100); return bitmap;
        }))
        {
            capture.FrameCaptured += frames.Enqueue; capture.Error += errors.Enqueue;
            await Task.Delay(130);
            Check(sourceCalls == 0 && frames.IsEmpty && !capture.Running, "Screen capture does not read pixels or emit frames before explicit Start");
            var clock = Stopwatch.StartNew(); capture.Start();
            await Until(() => frames.Count >= 3, "Synthetic screen source emits a sequence of live frames without accessing the desktop", 8000);
            await capture.StopAsync(); clock.Stop();
            var stoppedCount = frames.Count; var stoppedCalls = sourceCalls;
            await Task.Delay(220);
            Check(frames.Count == stoppedCount && sourceCalls == stoppedCalls && !capture.Running,
                "Stopping screen capture halts both source reads and outgoing frames");
            Check(capture.FramesCaptured == stoppedCount && stoppedCount <= Math.Ceiling(clock.Elapsed.TotalSeconds * options.FramesPerSecond) + 1,
                "Synthetic capture frequency stays within the configured frame rate");
            Check(frames.All(jpeg => jpeg.Length <= ScreenCaptureService.TargetBytesPerSecond / options.FramesPerSecond) && capture.BytesEncoded == frames.Sum(jpeg => (long)jpeg.Length),
                "Live capture obeys its encoded byte budget and reports bounded frame sizes");
            Check(!frames.First().SequenceEqual(frames.Last()), "Synthetic capture sends fresh frames rather than repeating its first bitmap");
            Check(errors.IsEmpty, "Synthetic screen capture stops without encoder errors");
        }
        var failure = new ConcurrentQueue<string>();
        await using (var broken = new ScreenCaptureService(options, () => throw new InvalidOperationException("synthetic source failure")))
        {
            broken.Error += failure.Enqueue; broken.Start();
            await Until(() => failure.Count == 1, "Capture source failure is surfaced to the owning UI", 5000);
            await broken.StopAsync();
            Check(broken.FramesCaptured == 0 && !broken.Running && failure.Single().Contains("synthetic source failure", StringComparison.Ordinal),
                "Failed capture stops cleanly without emitting a stale frame");
        }
    }
}
