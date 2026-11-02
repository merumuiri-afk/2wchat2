using TwoWChat.Audio;

namespace TwoWChat.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly ComboBox _input = new(), _output = new(), _mode = new(), _talkKey = new(), _jitter = new(), _quality = new();
    private readonly TrackBar _gain = new(), _volume = new(), _threshold = new();
    private readonly CheckBox _gate = new();
    private readonly CheckBox _messageNotifications = new(), _notificationSound = new();
    private readonly Button _testNotification = new();
    private readonly MessageNotificationSound _notificationPreview = new();
    private readonly TrackBar _notificationVolume = new();
    private readonly Label _notificationVolumeText = new();
    private readonly Label _gainText = new(), _volumeText = new(), _thresholdText = new(), _testStatus = new();
    private readonly ProgressBar _meter = new();
    private readonly Button _test = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 80 };
    private AudioEngine? _monitor;
    private readonly bool _connected;
    private bool _monitorRequested;
    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings settings, bool connected)
    {
        _settings = settings;
        _connected = connected;
        Result = settings;
        Text = "2WChat — настройки звука";
        Font = new Font("Tahoma", 9);
        BackColor = SystemColors.Control;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(580, 680);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 3, RowCount = 18 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        Controls.Add(grid);
        foreach (var box in new[] { _input, _output, _mode, _talkKey, _jitter, _quality }) { box.DropDownStyle = ComboBoxStyle.DropDownList; box.Dock = DockStyle.Fill; }
        var refresh = new Button { Text = "↻", Dock = DockStyle.Fill, AccessibleName = "Обновить устройства" };
        refresh.Click += (_, _) => RefreshDevices();
        AddRow(grid, 0, "Микрофон:", _input, refresh);
        AddRow(grid, 1, "Вывод звука:", _output);
        _gain.Minimum = 0; _gain.Maximum = 200; _gain.TickFrequency = 50; _gain.Value = settings.InputGain;
        _volume.Minimum = 0; _volume.Maximum = 150; _volume.TickFrequency = 25; _volume.Value = settings.OutputVolume;
        foreach (var slider in new[] { _gain, _volume, _threshold }) { slider.Dock = DockStyle.Fill; slider.Height = 36; }
        AddRow(grid, 2, "Усиление микрофона:", _gain, _gainText, 44);
        AddRow(grid, 3, "Громкость собеседников:", _volume, _volumeText, 44);
        _mode.Items.AddRange(["Открытый микрофон", "Нажми и говори"]); _mode.SelectedIndex = settings.PushToTalk ? 1 : 0;
        AddRow(grid, 4, "Передача голоса:", _mode);
        _talkKey.Items.AddRange([new KeyChoice(Keys.F8, "F8"), new KeyChoice(Keys.F9, "F9"), new KeyChoice(Keys.F10, "F10"), new KeyChoice(Keys.RControlKey, "Правый Ctrl"), new KeyChoice(Keys.RMenu, "Правый Alt")]);
        _talkKey.SelectedIndex = Math.Max(0, _talkKey.Items.Cast<KeyChoice>().ToList().FindIndex(k => (int)k.Key == settings.TalkKey));
        AddRow(grid, 5, "Клавиша разговора:", _talkKey);
        _gate.Text = "Приглушать тихий фон между репликами"; _gate.Checked = settings.NoiseGate; _gate.AutoSize = true;
        grid.Controls.Add(_gate, 0, 6); grid.SetColumnSpan(_gate, 3); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _threshold.Minimum = -60; _threshold.Maximum = -20; _threshold.TickFrequency = 10; _threshold.Value = settings.GateDb;
        AddRow(grid, 7, "Шумовой порог:", _threshold, _thresholdText, 44);
        var explanation = new Label { Text = "Это простой шумовой затвор. Фон во время речи остаётся.\nДля защиты от эха используйте наушники.", Dock = DockStyle.Fill, AutoSize = false, ForeColor = SystemColors.GrayText };
        grid.Controls.Add(explanation, 0, 8); grid.SetColumnSpan(explanation, 3); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _quality.Items.AddRange(["24 кбит/с — экономно", "32 кбит/с — обычно", "48 кбит/с — выше качество"]);
        _quality.SelectedIndex = settings.Bitrate == 24000 ? 0 : settings.Bitrate == 48000 ? 2 : 1;
        AddRow(grid, 9, "Качество Opus:", _quality);
        _jitter.Items.AddRange(["40 мс — стабильная сеть", "60 мс — обычно", "100 мс — нестабильная сеть", "160 мс — большие колебания"]);
        _jitter.SelectedIndex = Array.IndexOf(new[] { 40, 60, 100, 160 }, settings.JitterMs);
        AddRow(grid, 10, "Буфер сети:", _jitter);
        _test.Text = "Тест микрофона"; _test.AutoSize = true;
        _test.Click += (_, _) => ToggleTest();
        grid.Controls.Add(_test, 0, 11);
        _meter.Dock = DockStyle.Fill; _meter.Style = ProgressBarStyle.Continuous;
        grid.Controls.Add(_meter, 1, 11); grid.SetColumnSpan(_meter, 2); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _testStatus.Text = connected ? "Для проверки микрофона сначала выйдите из комнаты." : "При проверке вы услышите себя с небольшой задержкой.";
        _test.Enabled = !connected;
        _testStatus.Dock = DockStyle.Fill;
        grid.Controls.Add(_testStatus, 0, 12); grid.SetColumnSpan(_testStatus, 3); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var hint = new Label { Text = "Изменения вступают в силу после нажатия «Применить».\nF6 — микрофон; F7 — звук (когда окно чата активно).", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
        grid.Controls.Add(hint, 0, 13); grid.SetColumnSpan(hint, 3); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var notifications = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        _messageNotifications.Text = "Уведомления о новых сообщениях"; _messageNotifications.AutoSize = true; _messageNotifications.Checked = settings.MessageNotifications;
        var soundRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _notificationSound.Text = "Звук уведомления"; _notificationSound.AutoSize = true; _notificationSound.Checked = settings.MessageNotificationSound;
        _testNotification.Text = "Тест звука"; _testNotification.AutoSize = true;
        soundRow.Controls.AddRange([_notificationSound, _testNotification]);
        notifications.Controls.AddRange([_messageNotifications, soundRow]);
        grid.Controls.Add(notifications, 0, 14); grid.SetColumnSpan(notifications, 3); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        _notificationVolume.Minimum = 0; _notificationVolume.Maximum = 150; _notificationVolume.TickFrequency = 25;
        _notificationVolume.Value = settings.EffectiveNotificationVolume; _notificationVolume.Dock = DockStyle.Fill;
        _notificationVolume.AccessibleName = "Громкость уведомлений";
        AddRow(grid, 15, "Громкость уведомлений:", _notificationVolume, _notificationVolumeText, 40);
        _notificationVolumeText.Text = _notificationVolume.Value + "%";
        _notificationVolume.ValueChanged += (_, _) =>
        {
            _notificationPreview.Stop(); _notificationVolumeText.Text = _notificationVolume.Value + "%"; UpdateNotificationOptions();
        };
        _messageNotifications.CheckedChanged += (_, _) => UpdateNotificationOptions();
        _notificationSound.CheckedChanged += (_, _) => UpdateNotificationOptions();
        _testNotification.Click += (_, _) =>
        {
            StopTest();
            _notificationPreview.TryPlay((_output.SelectedItem as AudioDevice)?.Number ?? -1, _notificationVolume.Value / 100f);
        };
        _notificationPreview.Error += text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _testStatus.Text = text); };
        UpdateNotificationOptions();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var apply = new Button { Text = "Применить", AutoSize = true };
        apply.Click += (_, _) => { StopTest(); Result = Collect(); DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.AddRange([cancel, apply]);
        grid.Controls.Add(buttons, 0, 16); grid.SetColumnSpan(buttons, 3); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        AcceptButton = apply; CancelButton = cancel;
        _gain.ValueChanged += (_, _) => UpdatePreview(); _volume.ValueChanged += (_, _) => UpdatePreview(); _threshold.ValueChanged += (_, _) => UpdatePreview(); _gate.CheckedChanged += (_, _) => UpdatePreview();
        _timer.Tick += (_, _) => _meter.Value = Math.Clamp((int)((_monitor?.InputLevel ?? 0) * 300), 0, 100);
        RefreshDevices(); UpdatePreview();
        FormClosed += (_, _) => { StopTest(); _notificationPreview.Dispose(); _timer.Dispose(); };
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control, Control? suffix = null, int height = 34)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        grid.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        grid.Controls.Add(control, 1, row);
        if (suffix != null) { suffix.Dock = DockStyle.Fill; grid.Controls.Add(suffix, 2, row); }
        else grid.SetColumnSpan(control, 2);
    }
    private void RefreshDevices()
    {
        StopTest();
        _notificationPreview.Stop();
        FillDevices(_input, AudioEngine.InputDevices(), _settings.InputDevice);
        FillDevices(_output, AudioEngine.OutputDevices(), _settings.OutputDevice);
    }
    private static void FillDevices(ComboBox box, AudioDevice[] devices, string selected)
    {
        box.Items.Clear(); box.Items.AddRange(devices);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(devices, d => d.Name == selected));
    }
    private AppSettings Collect()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings))!;
        s.InputDevice = (_input.SelectedItem as AudioDevice)?.Name ?? "";
        s.OutputDevice = (_output.SelectedItem as AudioDevice)?.Name ?? "";
        s.InputGain = _gain.Value; s.OutputVolume = _volume.Value; s.NoiseGate = _gate.Checked; s.GateDb = _threshold.Value;
        s.PushToTalk = _mode.SelectedIndex == 1; s.TalkKey = (int)((KeyChoice)_talkKey.SelectedItem!).Key;
        s.JitterMs = new[] { 40, 60, 100, 160 }[Math.Max(0, _jitter.SelectedIndex)];
        s.Bitrate = new[] { 24000, 32000, 48000 }[Math.Max(0, _quality.SelectedIndex)];
        s.MessageNotifications = _messageNotifications.Checked; s.MessageNotificationSound = _notificationSound.Checked;
        s.NotificationVolume = _notificationVolume.Value;
        return s;
    }
    private void UpdateNotificationOptions()
    {
        _notificationSound.Enabled = _messageNotifications.Checked;
        _notificationVolume.Enabled = _messageNotifications.Checked && _notificationSound.Checked;
        _testNotification.Enabled = _messageNotifications.Checked && _notificationSound.Checked && !_settings.Deafened && _notificationVolume.Value > 0;
        if (!_testNotification.Enabled) _notificationPreview.Stop();
    }
    private void UpdatePreview()
    {
        _gainText.Text = _gain.Value + "%"; _volumeText.Text = _volume.Value + "%"; _thresholdText.Text = _threshold.Value + " дБ";
        UpdateNotificationOptions();
        if (_monitor == null) return;
        _monitor.MicrophoneGain = _gain.Value / 100f; _monitor.Mixer.Volume = _volume.Value / 100f; _monitor.GateDb = _threshold.Value; _monitor.GateEnabled = _gate.Checked;
    }
    private void ToggleTest()
    {
        if (_connected) return;
        if (_monitorRequested) { StopTest(); return; }
        _monitorRequested = true;
        _test.Text = "Остановить тест";
        _testStatus.Text = "Скажите что-нибудь. Вы слышите себя только на этом компьютере.";
        _monitor = new AudioEngine(40, true);
        _monitor.Error += text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _testStatus.Text = text); };
        UpdatePreview();
        _monitor.Start((_input.SelectedItem as AudioDevice)?.Number ?? -1, (_output.SelectedItem as AudioDevice)?.Number ?? -1);
        _timer.Start();
    }
    private void StopTest()
    {
        _timer.Stop(); _monitor?.Dispose(); _monitor = null; _monitorRequested = false;
        _test.Text = "Тест микрофона"; _meter.Value = 0;
    }
    private sealed record KeyChoice(Keys Key, string Label) { public override string ToString() => Label; }
}
