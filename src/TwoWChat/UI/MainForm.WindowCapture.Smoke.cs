using TwoWChat.ScreenSharing;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private async Task RunWindowCaptureSmokeAsync(string directory, List<string> results)
    {
        using var fixture = new Form { Text = "2WChat generated window capture fixture", ClientSize = new Size(320, 200), StartPosition = FormStartPosition.Manual, Location = new Point(100, 100), BackColor = Color.Navy };
        fixture.Paint += (_, e) => e.Graphics.FillRectangle(Brushes.Gold, 20, 20, 160, 100);
        fixture.Show(this); await Task.Delay(150);
        var handle = fixture.Handle;
        var target = ScreenSharing.WindowCapture.List().Single(w => w.Handle == handle);
        using (var selector = new ScreenShareSelectionForm())
        {
            selector.Show(this); await Task.Delay(100);
            var sources = DescendantControls(selector).OfType<ComboBox>().Single(c => c.Items.Cast<object>().Any(i => i.ToString()!.Contains(fixture.Text)));
            sources.SelectedIndex = sources.Items.Cast<object>().Select((item, index) => (item, index)).Single(x => x.item.ToString()!.Contains(fixture.Text)).index;
            if (selector.Options.WindowHandle != handle || selector.Options.WindowProcessId != target.ProcessId) throw new IOException("Window selection did not retain target identity");
            using var shot = new Bitmap(selector.Width, selector.Height); selector.DrawToBitmap(shot, new Rectangle(Point.Empty, selector.Size)); shot.Save(Path.Combine(directory, "window-selection.png")); selector.Close();
        }
        using var cover = new Form { Text = "Generated covering fixture", Bounds = fixture.Bounds, BackColor = Color.Red, StartPosition = FormStartPosition.Manual };
        cover.Show(this); await Task.Delay(100);
        using (var frame = await Task.Run(() => WindowCapture.Capture(handle, target.ProcessId)))
        {
            var gold = 0;
            for (var y = 0; y < frame.Height; y += 4) for (var x = 0; x < frame.Width; x += 4)
            { var pixel = frame.GetPixel(x, y); if (pixel.R > 240 && pixel.G > 190 && pixel.B < 30) gold++; }
            if (gold < 200) throw new IOException("Native window capture did not contain the generated fixture pixels");
            frame.Save(Path.Combine(directory, "window-capture-fixture.png"));
        }
        cover.Close();
        results.Add("Source picker selects a native window and captures its generated pixels while another generated window covers it");
        var frames = 0;
        await using (var capture = new ScreenCaptureService(new ScreenCaptureOptions(target.Bounds, 5, 60, false, handle, target.ProcessId)))
        {
            capture.FrameCaptured += _ => Interlocked.Increment(ref frames); capture.Start();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (frames < 2 && wait.ElapsedMilliseconds < 5000) await Task.Delay(30);
            await capture.StopAsync(); if (frames < 2) throw new IOException("Live native window capture did not emit frames");
        }
        results.Add("Live capture service encodes multiple frames from the chosen window and stops cleanly");
        fixture.WindowState = FormWindowState.Minimized; await Task.Delay(100);
        var rejected = false;
        try { using var ignored = WindowCapture.Capture(handle, target.ProcessId); } catch (IOException) { rejected = true; }
        if (!rejected) throw new IOException("Minimized window was not rejected");
        fixture.Close();
        rejected = false;
        try { using var ignored = WindowCapture.Capture(handle, target.ProcessId); } catch (IOException) { rejected = true; }
        if (!rejected) throw new IOException("Closed window was not rejected");
        results.Add("Minimized and closed targets fail explicitly without reading desktop pixels");
    }
}
