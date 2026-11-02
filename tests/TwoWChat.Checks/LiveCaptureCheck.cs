using System.Collections.Concurrent;
using TwoWChat.ScreenSharing;

internal static partial class Program
{
    // Explicit opt-in device probe, separate from deterministic automated tests.
    // Pixels stay in this process's RAM: no viewer, network send, or image file.
    private static async Task CheckCaptureDeviceAsync()
    {
        var screen = Screen.PrimaryScreen ?? throw new IOException("No primary display");
        var errors = new ConcurrentQueue<string>(); var frames = 0;
        await using var capture = new ScreenCaptureService(new ScreenCaptureOptions(screen.Bounds, 5));
        capture.FrameCaptured += _ => Interlocked.Increment(ref frames);
        capture.Error += errors.Enqueue;
        capture.Start();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (frames < 2 && errors.IsEmpty && wait.ElapsedMilliseconds < 4000) await Task.Delay(40);
        await capture.StopAsync();
        var success = frames >= 1 && errors.IsEmpty;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { CaptureAvailable = success, Frames = frames, Errors = errors.ToArray(), SavedOrTransmitted = false }));
        Environment.ExitCode = success ? 0 : 1;
    }
}
