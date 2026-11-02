using System.ComponentModel;
using System.Runtime.InteropServices;
using TwoWChat.ScreenSharing;

namespace TwoWChat.UI;

public sealed class ScreenShareSelectionForm : Form
{
    private readonly ComboBox _monitor = new(), _fps = new(), _quality = new();
    private readonly CheckBox _cursor = new() { Text = "Показывать указатель мыши", Checked = true, AutoSize = true };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(55, 55, 55) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, Text = "Предпросмотр появится только после нажатия кнопки.", TextAlign = ContentAlignment.MiddleLeft };
    public ScreenCaptureOptions Options => new(((MonitorChoice)_monitor.SelectedItem!).Bounds, _fps.SelectedIndex == 0 ? 5 : 10, _quality.SelectedIndex == 0 ? 45 : 65, _cursor.Checked, ((MonitorChoice)_monitor.SelectedItem!).Handle, ((MonitorChoice)_monitor.SelectedItem!).ProcessId);
    public string SelectedTitle => ((MonitorChoice)_monitor.SelectedItem!).Label;

    public ScreenShareSelectionForm()
    {
        Text = "2WChat — демонстрация экрана"; Font = new Font("Tahoma", 9); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(670, 520); MinimumSize = new Size(560, 450); StartPosition = FormStartPosition.CenterParent;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 8 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); Controls.Add(grid);
        foreach (var combo in new[] { _monitor, _fps, _quality }) { combo.Dock = DockStyle.Fill; combo.DropDownStyle = ComboBoxStyle.DropDownList; }
        var screens = Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();
        for (var i = 0; i < screens.Length; i++) _monitor.Items.Add(new MonitorChoice(screens[i].Bounds, $"Экран {i + 1} — {screens[i].Bounds.Width}×{screens[i].Bounds.Height}" + (screens[i].Primary ? " (основной)" : "")));
        foreach (var window in WindowCapture.List()) _monitor.Items.Add(new MonitorChoice(window.Bounds, "Окно — " + window.Title, window.Handle, window.ProcessId));
        if (_monitor.Items.Count > 0) _monitor.SelectedIndex = 0;
        _fps.Items.AddRange(["5 кадров/с — текст и слабая сеть", "10 кадров/с — обычный режим"]); _fps.SelectedIndex = 1;
        _quality.Items.AddRange(["Экономно", "Чётче"]); _quality.SelectedIndex = 1;
        AddField(grid, 0, "Экран / окно:", _monitor); AddField(grid, 1, "Частота:", _fps); AddField(grid, 2, "Качество:", _quality);
        grid.Controls.Add(_cursor, 0, 3); grid.SetColumnSpan(_cursor, 2);
        var preview = new Button { Text = "Показать предпросмотр", AutoSize = true };
        preview.Click += (_, _) => CapturePreview();
        var refresh = new Button { Text = "Обновить список окон", AutoSize = true };
        refresh.Click += (_, _) =>
        {
            var selected = (_monitor.SelectedItem as MonitorChoice)?.Handle;
            for (var index = _monitor.Items.Count - 1; index >= 0; index--) if (((MonitorChoice)_monitor.Items[index]!).Handle != IntPtr.Zero) _monitor.Items.RemoveAt(index);
            foreach (var window in WindowCapture.List()) _monitor.Items.Add(new MonitorChoice(window.Bounds, "Окно — " + window.Title, window.Handle, window.ProcessId));
            var match = _monitor.Items.Cast<MonitorChoice>().FirstOrDefault(item => item.Handle == selected);
            if (match != null) _monitor.SelectedItem = match;
            else if (_monitor.Items.Count > 0) _monitor.SelectedIndex = 0;
        };
        var previews = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; previews.Controls.AddRange([preview, refresh]);
        grid.Controls.Add(previews, 0, 4); grid.SetColumnSpan(previews, 2);
        grid.Controls.Add(_preview, 0, 5); grid.SetColumnSpan(_preview, 2);
        _status.Text = "Выбранный экран будет виден участникам после нажатия «Начать».\nЗвук компьютера не передаётся; голос продолжает работать отдельно.";
        grid.Controls.Add(_status, 0, 6); grid.SetColumnSpan(_status, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var start = new Button { Text = "Начать демонстрацию", AutoSize = true, Enabled = _monitor.Items.Count > 0 };
        start.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        actions.Controls.AddRange([cancel, start]); grid.Controls.Add(actions, 0, 7); grid.SetColumnSpan(actions, 2); AcceptButton = start; CancelButton = cancel;
        _monitor.SelectedIndexChanged += (_, _) => { var old = _preview.Image; _preview.Image = null; old?.Dispose(); _cursor.Enabled = _monitor.SelectedItem is MonitorChoice { Handle: var h } && h == IntPtr.Zero; _status.Text = _cursor.Enabled ? "Будет показан выбранный монитор." : "Проверьте предпросмотр: игры и GPU-приложения могут давать чёрный кадр. Сворачивание остановит показ."; };
        FormClosed += (_, _) => { var old = _preview.Image; _preview.Image = null; old?.Dispose(); };
    }
    private static void AddField(TableLayoutPanel grid, int row, string label, Control field)
    { grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); grid.Controls.Add(field, 1, row); }
    private void CapturePreview()
    {
        if (_monitor.SelectedItem == null) return;
        try
        {
            var bitmap = Options.WindowHandle == IntPtr.Zero ? ScreenCaptureService.CapturePreview(Options.Bounds, Options.IncludeCursor) : WindowCapture.Capture(Options.WindowHandle, Options.WindowProcessId);
            var old = _preview.Image; _preview.Image = bitmap; old?.Dispose();
            _status.Text = "Локальный предпросмотр. Передача ещё не началась.\nВыбранный источник: " + SelectedTitle + ". Звук компьютера не передаётся.";
        }
        catch (Exception e) when (e is Win32Exception or ExternalException or ArgumentException or IOException)
        { _status.Text = "Не удалось получить предпросмотр: " + e.Message; }
    }
    private sealed record MonitorChoice(Rectangle Bounds, string Label, IntPtr Handle = default, uint ProcessId = 0) { public override string ToString() => Label; }
}
