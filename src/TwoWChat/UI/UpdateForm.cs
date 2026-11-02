using TwoWChat.Updates;

namespace TwoWChat.UI;

internal sealed class UpdateForm : Form
{
    private readonly UpdateRelease _release;
    private readonly Button _install = new() { Text = "Скачать и установить", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Позднее", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, Text = "Настройки сохранятся. Программа перезапустится после установки." };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly CancellationTokenSource _stop = new();
    private bool _downloading, _installing;

    public UpdateForm(UpdateRelease release)
    {
        _release = release;
        Text = "2WChat — обновление " + release.Version.ToString(3);
        Font = new Font("Tahoma", 9); BackColor = Color.FromArgb(212, 208, 200);
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(540, 370); MinimumSize = new Size(500, 340);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Установлена: " + UpdateService.CurrentVersion.ToString(3) + "     Доступна: " + release.Version.ToString(3) + "\nИсточник: github.com/" + UpdateService.Repository }, 0, 0);
        root.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = release.Notes }, 0, 1);
        root.Controls.Add(_status, 0, 2); root.Controls.Add(_progress, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 5, 0, 0) };
        buttons.Controls.AddRange([_cancel, _install]); root.Controls.Add(buttons, 0, 4); Controls.Add(root);
        _cancel.Click += (_, _) => { if (_downloading) { _stop.Cancel(); _cancel.Enabled = false; } else Close(); };
        _install.Click += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => { if (_downloading && !_installing) { e.Cancel = true; _stop.Cancel(); } };
        FormClosed += (_, _) => _stop.Dispose();
    }

    private async Task InstallAsync()
    {
        _downloading = true; _install.Enabled = false; _cancel.Text = "Отменить";
        try
        {
            using var service = new UpdateService();
            _status.Text = "Загрузка и проверка SHA-256…";
            var folder = await service.DownloadAsync(_release, Path.Combine(AppStore.Folder, "updates"), new Progress<int>(value => { if (!IsDisposed) _progress.Value = Math.Clamp(value, 0, 100); }), _stop.Token);
            _stop.Token.ThrowIfCancellationRequested();
            UpdateInstaller.Launch(folder, _release);
            _installing = true; DialogResult = DialogResult.OK; Close();
        }
        catch (OperationCanceledException) { _status.Text = "Загрузка отменена. Прежняя версия сохранена."; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _status.Text = "Не удалось обновить: " + e.Message; AppStore.Log("Update download: " + e);
        }
        finally
        {
            _downloading = false;
            if (!_installing) { _cancel.Text = "Закрыть"; _cancel.Enabled = true; if (!_stop.IsCancellationRequested) _install.Enabled = true; }
        }
    }
}
