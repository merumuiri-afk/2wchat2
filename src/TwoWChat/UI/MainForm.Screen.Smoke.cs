using TwoWChat.Core;
using TwoWChat.ScreenSharing;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private async Task RunScreenUiSmokeAsync(string directory, List<string> results, RoomClient broadcaster)
    {
        if (_client?.SupportsScreenShare != true || !_shareScreen.Enabled || _watchScreen.Enabled || _screenCapture != null)
            throw new IOException("Screen controls are not idle by default");
        results.Add("Screen capture is inactive on joining and starts only by an explicit action");
        await broadcaster.StartScreenShareAsync("Тестовый экран", 640, 360);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!_watchScreen.Enabled && wait.ElapsedMilliseconds < 5000) await Task.Delay(30);
        if (!_watchScreen.Enabled) throw new IOException("Remote screen did not appear in the room UI");
        await ToggleScreenViewerAsync();
        var viewer = _screenViewer ?? throw new IOException("Screen viewer did not open");
        var phase = 0;
        await using var source = new ScreenCaptureService(new ScreenCaptureOptions(new Rectangle(0, 0, 640, 360)), () => DemoScreenFrame(phase++));
        source.FrameCaptured += jpeg => broadcaster.SendScreenFrame(jpeg);
        source.Start();
        wait.Restart();
        while (viewer.DisplayedFrameCount < 4 && wait.ElapsedMilliseconds < 7000) await Task.Delay(30);
        if (viewer.DisplayedFrameCount < 4 || viewer.DisplayedSize != new Size(640, 360)) throw new IOException("Live generated screen frames did not render in the viewer");
        results.Add("Selected screen viewing path renders multiple synthetic live frames from another client");
        using (var bitmap = new Bitmap(viewer.Width, viewer.Height))
        { viewer.DrawToBitmap(bitmap, new Rectangle(Point.Empty, viewer.Size)); bitmap.Save(Path.Combine(directory, "screen-viewer.png")); }
        using (var bitmap = new Bitmap(Width, Height))
        { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, "screen-live-room.png")); }
        using (var selection = new ScreenShareSelectionForm())
        {
            selection.Show(this); await Task.Delay(100);
            using var bitmap = new Bitmap(selection.Width, selection.Height);
            selection.DrawToBitmap(bitmap, new Rectangle(Point.Empty, selection.Size)); bitmap.Save(Path.Combine(directory, "screen-selection.png")); selection.Close();
        }
        results.Add("Monitor selection renders without capturing desktop pixels or automatically starting a share");
        await source.StopAsync(); await broadcaster.StopScreenShareAsync();
        wait.Restart();
        while ((viewer.DisplayedSize != Size.Empty || _client.Screens.Length != 0) && wait.ElapsedMilliseconds < 5000) await Task.Delay(30);
        if (viewer.DisplayedSize != Size.Empty || _client.Screens.Length != 0) throw new IOException("Stopped screen was not cleared from the viewer");
        results.Add("Stopping a broadcast clears its last frame and removes it from the room");
        await ToggleScreenViewerAsync();
        if (_screenViewer != null || _client.WatchingScreenId != null) throw new IOException("Screen viewer socket did not close with its window");
        results.Add("Closing the viewer releases its media subscription");
    }

    private static Bitmap DemoScreenFrame(int phase)
    {
        var bitmap = new Bitmap(640, 360); using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(10, 36, 106));
        graphics.FillRectangle(Brushes.WhiteSmoke, 20, 20, 600, 320);
        graphics.FillRectangle(Brushes.Gold, 35, 35, 570, 65);
        using var font = new Font("Tahoma", 22, FontStyle.Bold);
        graphics.DrawString("2WChat — прямой эфир", font, Brushes.Navy, 45, 48);
        graphics.FillEllipse(Brushes.SeaGreen, 40 + phase * 8 % 340, 140, 150, 120);
        graphics.DrawString("Кадр " + phase, font, Brushes.Navy, 390, 270);
        return bitmap;
    }
}
