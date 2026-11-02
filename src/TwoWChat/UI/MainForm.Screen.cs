using TwoWChat.Core;
using TwoWChat.ScreenSharing;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private ScreenCaptureService? _screenCapture;
    private ScreenViewerForm? _screenViewer;
    private Guid _screenViewerOwner;
    private bool _screenChanging, _screenClosing;
    private int _screenGeneration;

    private void AttachScreenEvents(RoomClient client)
    {
        client.ScreensChanged += _ => Post(() =>
        {
            if (_client != client) return;
            UpdateScreenControls();
            if (_screenViewer != null && !client.Screens.Any(s => s.OwnerId == _screenViewerOwner))
                _screenViewer.ClearFrame("Трансляция завершена.");
        });
        client.ScreenFrameReceived += frame => { if (_client == client && frame.OwnerId == _screenViewerOwner) _screenViewer?.UpdateFrame(frame.Jpeg); };
        client.ScreenShareEnded += reason => Post(async () =>
        {
            if (_client != client || client.IsScreenSharing || _screenCapture == null || _screenClosing) return;
            var capture = _screenCapture; _screenCapture = null; await capture.DisposeAsync();
            AppendNotice(reason); UpdateScreenControls();
        });
        client.ScreenWatchEnded += reason => Post(() =>
        {
            if (_client == client && client.WatchingScreenId == null) { _screenViewer?.ClearFrame(reason); UpdateScreenControls(); }
        });
    }

    private void UpdateScreenControls()
    {
        var client = _client; var ready = client?.Connected == true && client.SupportsScreenShare;
        var stream = ready ? client!.Screens.FirstOrDefault() : null;
        var own = _screenCapture != null || (stream != null && stream.OwnerId == client?.Id);
        _shareScreen.Text = own ? "Остановить показ" : "Демонстрация экрана…";
        _shareScreen.Enabled = ready && !_screenChanging && (stream == null || own);
        _watchScreen.Text = _screenViewer != null ? "Закрыть просмотр" : "Смотреть трансляцию";
        _watchScreen.Enabled = ready && !_screenChanging && (_screenViewer != null || (stream != null && !own));
        _screenStatus.Text = stream == null ? "Нет активной трансляции" : own ? "Вы показываете: " + stream.Title : stream.Name + " показывает: " + stream.Title;
        _screenStatus.ForeColor = own ? Color.DarkRed : Color.DarkGreen;
        _tips.SetToolTip(_shareScreen, client?.Connected == true && !client.SupportsScreenShare ? "Владелец комнаты должен обновиться до версии 1.4." : "Выберите монитор. Показ начинается только после нажатия «Начать».");
    }

    private async Task ToggleScreenShareAsync()
    {
        var client = _client;
        if (_screenChanging || client?.Connected != true || !client.SupportsScreenShare) return;
        if (_screenCapture != null || client.IsScreenSharing)
        {
            _screenChanging = true; UpdateScreenControls();
            try
            {
                var capture = _screenCapture; _screenCapture = null;
                if (capture != null) await capture.DisposeAsync();
                await client.StopScreenShareAsync();
            }
            catch (Exception e) when (ScreenOperationError(e)) { AppendNotice(e.Message); }
            finally { _screenChanging = false; UpdateScreenControls(); }
            return;
        }
        using var selection = new ScreenShareSelectionForm();
        if (selection.ShowDialog(this) != DialogResult.OK) return;
        if (_client != client || !client.Connected) return;
        _screenChanging = true; var generation = _screenGeneration; UpdateScreenControls();
        try
        {
            var bounds = selection.Options.Bounds;
            var scale = Math.Min(1d, Math.Min(1280d / bounds.Width, 720d / bounds.Height));
            await client.StartScreenShareAsync(selection.SelectedTitle, Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)));
            if (_client != client || generation != _screenGeneration) { await client.StopScreenShareAsync(); return; }
            var capture = new ScreenCaptureService(selection.Options); _screenCapture = capture;
            capture.FrameCaptured += jpeg => { if (_screenCapture == capture && _client == client) client.SendScreenFrame(jpeg); };
            capture.Error += error => Post(async () =>
            {
                if (_screenCapture != capture) return;
                AppendNotice("Захват экрана остановлен: " + error);
                await ToggleScreenShareAsync();
            });
            capture.Start();
            AppendNotice("Демонстрация запущена: " + selection.SelectedTitle + ". Кнопка «Остановить показ» завершает трансляцию.");
        }
        catch (Exception e) when (ScreenOperationError(e))
        {
            var capture = _screenCapture; _screenCapture = null; if (capture != null) await capture.DisposeAsync();
            try { await client.StopScreenShareAsync(); } catch (Exception stopError) when (ScreenOperationError(stopError)) { }
            if (_client == client) AppendNotice("Не удалось начать демонстрацию: " + e.Message);
        }
        finally { _screenChanging = false; UpdateScreenControls(); }
    }

    private async Task ToggleScreenViewerAsync()
    {
        if (_screenViewer != null) { _screenViewer.Close(); return; }
        var client = _client; var stream = client?.Screens.FirstOrDefault(s => s.OwnerId != client.Id);
        if (_screenChanging || client?.Connected != true || stream == null) return;
        _screenChanging = true;
        var viewer = new ScreenViewerForm(stream.Name + " — " + stream.Title);
        _screenViewer = viewer; _screenViewerOwner = stream.OwnerId;
        viewer.FormClosed += async (_, _) =>
        {
            if (_screenViewer != viewer) return;
            _screenViewer = null; _screenViewerOwner = Guid.Empty;
            if (!_screenClosing)
                try { await client.StopWatchingScreenAsync(); } catch (Exception e) when (ScreenOperationError(e)) { }
            if (!IsDisposed) UpdateScreenControls();
        };
        viewer.Show(this); viewer.SetStatus("Подключение к трансляции…"); UpdateScreenControls();
        try
        {
            await client.WatchScreenAsync(stream.OwnerId);
            if (_screenViewer != viewer || _client != client) await client.StopWatchingScreenAsync();
            else viewer.ResumeFrames();
        }
        catch (Exception e) when (ScreenOperationError(e)) { if (!viewer.IsDisposed) viewer.ClearFrame("Не удалось подключиться: " + e.Message); }
        finally { _screenChanging = false; UpdateScreenControls(); }
    }
    private async Task CloseScreenResourcesAsync()
    {
        _screenGeneration++; _screenClosing = true;
        try
        {
            var capture = _screenCapture; _screenCapture = null;
            if (capture != null) await capture.DisposeAsync();
            _screenViewer?.Close(); _screenViewer = null; _screenViewerOwner = Guid.Empty;
        }
        finally { _screenClosing = false; }
    }
    private static bool ScreenOperationError(Exception e) => e is IOException or InvalidOperationException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or System.ComponentModel.Win32Exception;
}
