using System.Runtime.InteropServices;
using TwoWChat.Core;
using TwoWChat.ScreenSharing;

namespace TwoWChat.UI;

public sealed class ScreenViewerForm : Form
{
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 35, 35) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 27, Text = "Ожидание кадров…", TextAlign = ContentAlignment.MiddleLeft };
    private readonly object _gate = new();
    private byte[]? _pending;
    private Bitmap? _ready;
    private bool _decoding, _presentScheduled, _closed, _receiving = true;
    private long _generation;
    private bool _fullscreen;
    private Rectangle _normalBounds;
    private FormWindowState _normalState;
    public long DisplayedFrameCount { get; private set; }
    public Size DisplayedSize { get; private set; }

    public ScreenViewerForm(string title)
    {
        Text = "2WChat — экран: " + Protocol.CleanName(title, 70); Font = new Font("Tahoma", 9); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 580); MinimumSize = new Size(480, 300); StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_picture); Controls.Add(_status);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(3), WrapContents = false };
        var fullscreen = new Button { Text = "На весь экран [F11]", AutoSize = true }; fullscreen.Click += (_, _) => ToggleFullscreen();
        var close = new Button { Text = "Закрыть просмотр", AutoSize = true }; close.Click += (_, _) => Close();
        top.Controls.AddRange([fullscreen, close]); Controls.Add(top);
        Shown += (_, _) => SchedulePresentation();
    }

    // Only the newest pending JPEG and decoded bitmap are retained, even while
    // the UI is busy or the viewer is minimized.
    public void UpdateFrame(byte[] jpeg)
    {
        if (jpeg.Length is < 24 or > ScreenCaptureService.MaxFrameBytes) return;
        lock (_gate)
        {
            if (_closed || !_receiving) return;
            _pending = jpeg;
            if (_decoding) return;
            _decoding = true;
        }
        _ = Task.Run(DecodeLoop);
    }
    private void DecodeLoop()
    {
        while (true)
        {
            byte[]? jpeg; long generation;
            lock (_gate)
            {
                jpeg = _pending; _pending = null; generation = _generation;
                if (_closed || !_receiving || jpeg == null) { _decoding = false; return; }
            }
            Bitmap? bitmap = null;
            try
            {
                var (type, width, height) = ImageAttachments.Inspect(jpeg);
                if (type != "image/jpeg" || width > ScreenCaptureService.MaxWidth || height > ScreenCaptureService.MaxHeight) throw new InvalidDataException("Недопустимый кадр демонстрации.");
                using var stream = new MemoryStream(jpeg, false); using var decoded = Image.FromStream(stream, false, true);
                if (decoded.Width != width || decoded.Height != height) throw new InvalidDataException("Повреждённый кадр демонстрации.");
                bitmap = new Bitmap(decoded);
                lock (_gate)
                {
                    if (_closed || !_receiving || generation != _generation) { bitmap.Dispose(); bitmap = null; continue; }
                    _ready?.Dispose(); _ready = bitmap; bitmap = null;
                }
                SchedulePresentation();
            }
            catch (Exception e) when (e is ArgumentException or ExternalException or IOException or OutOfMemoryException)
            { bitmap?.Dispose(); }
        }
    }
    private void SchedulePresentation()
    {
        lock (_gate)
        {
            if (_closed || _ready == null || _presentScheduled || !IsHandleCreated) return;
            _presentScheduled = true;
        }
        try { BeginInvoke(PresentLatest); }
        catch (InvalidOperationException) { lock (_gate) _presentScheduled = false; }
    }
    private void PresentLatest()
    {
        Bitmap? bitmap;
        lock (_gate)
        {
            bitmap = _ready; _ready = null; _presentScheduled = false;
            if (_closed || !_receiving) { bitmap?.Dispose(); return; }
        }
        if (bitmap == null) return;
        var old = _picture.Image; _picture.Image = bitmap; old?.Dispose(); DisplayedFrameCount++; DisplayedSize = bitmap.Size;
        _status.Text = $"{bitmap.Width}×{bitmap.Height}  •  Прямой эфир  •  F11 — весь экран, Esc — закрыть";
    }
    public void SetStatus(string text) => OnUi(() => _status.Text = text);
    public void ClearFrame(string status = "Трансляция завершена")
    {
        lock (_gate)
        {
            _generation++; _receiving = false; _pending = null; _ready?.Dispose(); _ready = null;
        }
        OnUi(() => { var old = _picture.Image; _picture.Image = null; old?.Dispose(); DisplayedSize = Size.Empty; _status.Text = status; });
    }
    public void ResumeFrames(string status = "Ожидание кадров…")
    { lock (_gate) { if (_closed) return; _generation++; _receiving = true; } SetStatus(status); }
    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (!InvokeRequired) { action(); return; }
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _normalBounds = Bounds; _normalState = WindowState; WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds; _fullscreen = true;
        }
        else { FormBorderStyle = FormBorderStyle.Sizable; WindowState = FormWindowState.Normal; Bounds = _normalBounds; WindowState = _normalState; _fullscreen = false; }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { if (_fullscreen) ToggleFullscreen(); else Close(); return true; }
        if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate) { _closed = true; _generation++; _pending = null; _ready?.Dispose(); _ready = null; }
            var bitmap = _picture.Image; _picture.Image = null; bitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
