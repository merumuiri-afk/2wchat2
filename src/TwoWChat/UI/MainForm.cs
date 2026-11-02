using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TwoWChat.Audio;
using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed partial class MainForm : Form
{
    private AppSettings _settings;
    private readonly TextBox _name = new(), _host = new(), _password = new(), _room = new();
    private readonly NumericUpDown _port = new();
    private readonly Button _create = new(), _join = new(), _leave = new(), _send = new();
    private readonly Button _attachImage = new();
    private readonly Button _shareScreen = new(), _watchScreen = new();
    private readonly Label _screenStatus = new();
    private readonly CheckBox _mute = new(), _deafen = new();
    private readonly Button _audioSettings = new();
    private readonly ListView _people = new();
    private readonly ChatView _chat = new();
    private readonly TextBox _message = new();
    private readonly Label _heading = new(), _roomLabel = new(), _talkHint = new();
    private readonly ToolStripStatusLabel _connectionStatus = new(), _networkStatus = new(), _audioStatus = new();
    private readonly ToolStripProgressBar _inputMeter = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 25 };
    private readonly StringBuilder _transcript = new();
    private readonly HashSet<Guid> _localMutes = [];
    private readonly Dictionary<Guid, float> _userVolumes = [];
    private readonly List<ChatEntry> _pendingChats = [];
    private readonly ToolTip _tips = new();
    private readonly MessageNotifications _notifications;
    private RoomClient? _client;
    private RoomServer? _server;
    private X509Certificate2? _identity;
    private AudioEngine? _audio;
    private CancellationTokenSource? _connectStop;
    private bool _busy, _closing, _stopping, _historyReady, _closeRequested;
    private long _connectedAt;
    private int _ticks;
    private readonly string? _smokeDirectory;
    public MainForm(AppSettings settings, string? smokeDirectory = null)
    {
        _settings = settings; _smokeDirectory = smokeDirectory;
        Text = "2WChat — голос и сообщения";
        Font = new Font("Tahoma", 9);
        BackColor = Color.FromArgb(212, 208, 200);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(990, 690); MinimumSize = new Size(910, 650);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BuildWindow();
        _notifications = new MessageNotifications(this, Icon);
        _notifications.UnreadChanged += _ => UpdateWindowCaption();
        _notifications.Error += text => Post(() => AppendNotice(text));
        _notifications.OpenRequested += RestoreFromCompact;
        Activated += (_, _) => { if (WindowState != FormWindowState.Minimized) _notifications.ClearUnread(); };
        _name.Text = settings.Name; _host.Text = settings.Host; _port.Value = settings.Port; _room.Text = settings.RoomName;
        _mute.Checked = settings.MicMuted; _deafen.Checked = settings.Deafened;
        _timer.Tick += (_, _) => TickAudio(); _timer.Start();
        _create.Click += async (_, _) => await ConnectRoomAsync(true);
        _join.Click += async (_, _) => await ConnectRoomAsync(false);
        _leave.Click += async (_, _) => { _connectStop?.Cancel(); await StopSessionAsync(); };
        _send.Click += async (_, _) => await SendChatAsync();
        _attachImage.Click += async (_, _) => await ChooseImageAsync();
        _shareScreen.Click += async (_, _) => await ToggleScreenShareAsync();
        _watchScreen.Click += async (_, _) => await ToggleScreenViewerAsync();
        _chat.ImageOpenRequested += OpenImage;
        _chat.AvatarOpenRequested += ShowAvatar;
        _chat.StickerSaveRequested += SaveAsSticker;
        _people.DoubleClick += (_, _) => { if (SelectedPeer() is { } peer) ShowAvatar(peer.Id, peer.Name); };
        _chat.ImageRequested += info => { if (_client?.Connected == true) _client.RequestImage(info); else _chat.ImageFailed(info.Id, "Соединение с комнатой закрыто."); };
        _chat.Error += AppendNotice;
        InitializeImageDrop();
        _message.KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V && ClipboardHasImage()) { e.SuppressKeyPress = true; await PasteImageAsync(); }
            else if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await SendChatAsync(); }
        };
        _mute.CheckedChanged += async (_, _) => await ChangeStateAsync();
        _deafen.CheckedChanged += async (_, _) => await ChangeStateAsync();
        _audioSettings.Click += (_, _) => OpenSettings();
        FormClosing += OnClosing;
        FormClosed += (_, _) => { _updateStop.Cancel(); _notifications.Dispose(); _timer.Dispose(); _tips.Dispose(); _avatarIcons.Dispose(); };
        Shown += async (_, _) => { if (_smokeDirectory != null) await RunUiSmokeAsync(_smokeDirectory); else await CheckUpdatesAsync(false); };
        SetConnectedUi(false);
        AppendNotice("Добро пожаловать в 2WChat. Создайте комнату или введите IP её владельца.");
        AppendNotice("Через Radmin VPN: подключитесь к одной VPN-сети и используйте VPN-IP владельца комнаты.");
        if (_settings.PushToTalk) AppendNotice("Включён режим «нажми и говори». Клавиша: " + TalkKeyLabel());
    }

    private void BuildWindow()
    {
        var menu = new MenuStrip { BackColor = BackColor, Renderer = new ToolStripSystemRenderer() };
        var file = new ToolStripMenuItem("&Файл");
        file.DropDownItems.Add("Сохранить переписку…", null, (_, _) => ExportChat());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Выход", null, (_, _) => Close());
        var tools = new ToolStripMenuItem("&Сервис");
        tools.DropDownItems.Add("Настройки звука…", null, (_, _) => OpenSettings());
        tools.DropDownItems.Add("Мини-окно участников", null, (_, _) => EnterCompact());
        tools.DropDownItems.Add("Мои стикеры…", null, async (_, _) => await OpenStickersAsync());
        tools.DropDownItems.Add("Моя аватарка…", null, async (_, _) => await OpenAvatarAsync());
        tools.DropDownItems.Add("Предложить название комнаты…", null, async (_, _) => await ProposeRoomNameAsync());
        tools.DropDownItems.Add("Мой IP и подключение…", null, (_, _) => ShowAddresses());
        AddUpdateMenu(tools);
        var help = new ToolStripMenuItem("&Справка");
        help.DropDownItems.Add("Как подключиться", null, (_, _) => ShowHelp());
        help.DropDownItems.Add("О программе", null, (_, _) => MessageBox.Show(this, "2WChat " + Updates.UpdateService.CurrentVersion.ToString(3) + "\n\nГолосовой и текстовый чат для Windows, LAN и VPN.\nДо 16 участников. Один владелец комнаты.\n\nOpus / NAudio / .NET.\nНет рекламы, облачного сервера и телеметрии.\nОбновления: github.com/" + Updates.UpdateService.Repository + "\nЛицензии библиотек — в THIRD-PARTY-NOTICES.txt.", "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.AddRange([file, tools, help]); MainMenuStrip = menu;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8, 4, 8, 5) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(root); Controls.Add(menu);
        _heading.Text = "  2WChat  /  голос и сообщения"; _heading.Dock = DockStyle.Fill;
        _heading.BackColor = Color.FromArgb(10, 36, 106); _heading.ForeColor = Color.White;
        _heading.Font = new Font("Tahoma", 12, FontStyle.Bold); _heading.TextAlign = ContentAlignment.MiddleLeft; _heading.Margin = new Padding(0, 0, 0, 7);
        root.Controls.Add(_heading, 0, 0);
        var connect = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 2, Padding = new Padding(3), BorderStyle = BorderStyle.Fixed3D };
        foreach (var width in new[] { 60, 140, 85, 175, 60, 80, 80 }) connect.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        connect.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connect.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); connect.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddField(connect, 0, 0, "Имя:", _name); _name.MaxLength = 24; _name.AccessibleName = "Имя участника";
        AddField(connect, 2, 0, "IP хоста:", _host); _host.PlaceholderText = "Например, 26.1.2.3"; _host.AccessibleName = "IP хоста";
        _port.Minimum = 1024; _port.Maximum = 65535; AddField(connect, 4, 0, "Порт:", _port);
        AddField(connect, 6, 0, "Пароль:", _password); _password.UseSystemPasswordChar = true; _password.MaxLength = 128;
        AddField(connect, 0, 1, "Зал:", _room); _room.MaxLength = 40;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(3, 3, 0, 0) };
        _create.Text = "Создать комнату"; _join.Text = "Подключиться"; _leave.Text = "Отключиться";
        foreach (var button in new[] { _create, _join, _leave }) { button.AutoSize = true; button.Height = 28; actions.Controls.Add(button); }
        var ip = new Button { Text = "Мой IP…", AutoSize = true, Height = 28 }; ip.Click += (_, _) => ShowAddresses(); actions.Controls.Add(ip);
        connect.Controls.Add(actions, 2, 1); connect.SetColumnSpan(actions, 6);
        root.Controls.Add(connect, 0, 1);
        var screenBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _shareScreen.Text = "Демонстрация экрана…"; _shareScreen.Size = new Size(190, 30);
        _watchScreen.Text = "Смотреть трансляцию"; _watchScreen.Size = new Size(180, 30);
        _screenStatus.Size = new Size(550, 30); _screenStatus.TextAlign = ContentAlignment.MiddleLeft; _screenStatus.AutoEllipsis = true;
        screenBar.Controls.AddRange([_shareScreen, _watchScreen, _screenStatus]); root.Controls.Add(screenBar, 0, 2);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6, Margin = new Padding(0, 7, 0, 0) };
        split.Size = new Size(970, 380); split.SplitterDistance = 240; split.Panel1MinSize = 175; split.Panel2MinSize = 420;
        var participants = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.Fixed3D };
        var peopleTitle = new Label { Text = " Участники", Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        _people.Dock = DockStyle.Fill; _people.View = View.Details; _people.FullRowSelect = true; _people.HideSelection = false; _people.MultiSelect = false;
        _people.SmallImageList = _avatarIcons;
        _people.Columns.Add("Имя", 123); _people.Columns.Add("Статус", 105); _people.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _people.ContextMenuStrip = BuildPeopleMenu();
        participants.Controls.Add(_people); participants.Controls.Add(peopleTitle); split.Panel1.Controls.Add(participants);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        _roomLabel.Text = " Общий чат"; _roomLabel.Dock = DockStyle.Fill; _roomLabel.TextAlign = ContentAlignment.MiddleLeft;
        _chat.Dock = DockStyle.Fill; _chat.BackColor = Color.White; _chat.Font = new Font("Tahoma", 9); _chat.AccessibleName = "Переписка";
        var compose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 5, 0, 0) };
        compose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        _message.Multiline = true; _message.AcceptsReturn = true; _message.ScrollBars = ScrollBars.Vertical; _message.MaxLength = Protocol.MaxTextLength; _message.Dock = DockStyle.Fill; _message.AccessibleName = "Текст сообщения";
        _send.Text = "Отправить\nEnter"; _send.Dock = DockStyle.Fill;
        _attachImage.Text = "Картинка…\nCtrl+O"; _attachImage.Dock = DockStyle.Fill;
        var stickers = new Button { Text = "Стикеры", Dock = DockStyle.Fill }; stickers.Click += async (_, _) => await OpenStickersAsync();
        compose.Controls.Add(_message, 0, 0); compose.Controls.Add(_attachImage, 1, 0); compose.Controls.Add(stickers, 2, 0); compose.Controls.Add(_send, 3, 0);
        right.Controls.Add(_roomLabel, 0, 0); right.Controls.Add(_chat, 0, 1); right.Controls.Add(compose, 0, 2); split.Panel2.Controls.Add(right); root.Controls.Add(split, 0, 3);
        var audio = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        _mute.Appearance = Appearance.Button; _deafen.Appearance = Appearance.Button;
        foreach (var check in new[] { _mute, _deafen }) { check.AutoSize = false; check.Size = new Size(180, 30); check.TextAlign = ContentAlignment.MiddleCenter; }
        _audioSettings.Text = "Настройки звука…"; _audioSettings.Size = new Size(155, 30);
        _talkHint.AutoSize = false; _talkHint.Size = new Size(390, 30); _talkHint.TextAlign = ContentAlignment.MiddleLeft;
        audio.Controls.AddRange([_mute, _deafen, _audioSettings, _talkHint]); root.Controls.Add(audio, 0, 4);
        var status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, BackColor = BackColor };
        _connectionStatus.Spring = true; _connectionStatus.TextAlign = ContentAlignment.MiddleLeft;
        _networkStatus.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _audioStatus.Text = "Микрофон:"; _inputMeter.Size = new Size(80, 14); _inputMeter.Style = ProgressBarStyle.Continuous;
        status.Items.AddRange([_connectionStatus, _networkStatus, _audioStatus, _inputMeter]); root.Controls.Add(status, 0, 5);
        _tips.SetToolTip(_mute, "F6 — включить / выключить микрофон"); _tips.SetToolTip(_deafen, "F7 — отключить звук и передачу микрофона");
        _tips.SetToolTip(_message, "Enter — отправить; Shift+Enter — новая строка");
        _tips.SetToolTip(_password, "Владелец задаёт пароль при создании. Гости вводят тот же пароль.");
    }

    private static void AddField(TableLayoutPanel grid, int column, int row, string label, Control control)
    {
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, column, row);
        control.Dock = DockStyle.Fill; control.Margin = new Padding(2, 7, 4, 3); grid.Controls.Add(control, column + 1, row);
    }
}
