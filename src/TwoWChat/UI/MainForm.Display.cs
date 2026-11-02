using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using TwoWChat.Core;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private void UpdateWindowCaption()
    {
        var caption = _client?.Connected == true ? $"2WChat — {_client.Room} ({_name.Text})" : "2WChat — голос и сообщения";
        Text = _notifications.UnreadCount > 0 ? $"[{_notifications.UnreadCount}] {caption}" : caption;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);

    private void TickAudio()
    {
        if (_audio != null) _audio.TalkHeld = (GetAsyncKeyState(_settings.TalkKey) & 0x8000) != 0;
        if (++_ticks % 8 != 0) return;
        var client = _client;
        if (client?.Connected != true) return;
        _inputMeter.Value = Math.Clamp((int)((_audio?.InputLevel ?? 0) * 300), 0, 100);
        foreach (ListViewItem item in _people.Items)
        {
            if (item.Tag is not PeerInfo peer) continue;
            var speaking = peer.Id == client.Id ? _audio?.Transmitting == true : _audio?.Mixer.IsSpeaking(peer.Id) == true;
            var state = _localMutes.Contains(peer.Id) ? "У вас тихо" : peer.Deafened ? "Без звука" : peer.MicMuted ? "Микр. выкл." : speaking ? "Говорит" : "В сети";
            if (item.SubItems[1].Text != state) item.SubItems[1].Text = state;
            item.ForeColor = speaking && !peer.MicMuted && !peer.Deafened ? Color.DarkGreen : Color.Black;
        }
        var udp = client.UdpReady ? "Голос: UDP OK" : Environment.TickCount64 - _connectedAt < 6000 ? "Голос: проверка UDP…" : "UDP недоступен: проверьте порт / VPN";
        _networkStatus.Text = $"{udp} | {client.PingMs} мс";
        _talkHint.Text = _deafen.Checked ? " Звук и передача микрофона отключены" : _mute.Checked ? " Микрофон отключён" : _settings.PushToTalk ? " Удерживайте " + TalkKeyLabel() + " для разговора" : " Открытый микрофон";
    }

    private void UpdateAudioButtons()
    {
        _mute.Text = _mute.Checked ? "Микрофон: ВЫКЛ [F6]" : "Микрофон: ВКЛ [F6]";
        _deafen.Text = _deafen.Checked ? "Звук: ВЫКЛ [F7]" : "Звук: ВКЛ [F7]";
        _talkHint.Text = _settings.PushToTalk ? " Удерживайте " + TalkKeyLabel() + " для разговора" : " Открытый микрофон";
    }

    private string TalkKeyLabel() => (Keys)_settings.TalkKey switch { Keys.RControlKey => "правый Ctrl", Keys.RMenu => "правый Alt", var key => key.ToString() };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F6) { _mute.Checked = !_mute.Checked; return true; }
        if (keyData == Keys.F7) { _deafen.Checked = !_deafen.Checked; return true; }
        if (keyData == (Keys.Control | Keys.O)) { _ = ChooseImageAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RefreshMembers()
    {
        if (_client == null) return;
        var members = _client.Members;
        var selected = _people.SelectedItems.Count == 1 ? (_people.SelectedItems[0].Tag as PeerInfo)?.Id : null;
        var previous = _people.Items.Cast<ListViewItem>().Select(i => (PeerInfo)i.Tag!).ToDictionary(p => p.Id);
        _people.BeginUpdate(); _people.Items.Clear();
        foreach (var peer in members)
        {
            var item = new ListViewItem(peer.Name + (peer.Id == _client.Id ? " (вы)" : "")) { Tag = peer, Selected = selected == peer.Id };
            item.SubItems.Add(peer.Deafened ? "Без звука" : peer.MicMuted ? "Микр. выкл." : "В сети"); _people.Items.Add(item);
        }
        _people.EndUpdate();
        RefreshAvatarImages();
        _audio?.Mixer.SyncMembers(members.Select(p => p.Id));
        _heading.Text = $"  2WChat  /  {_client.Room}  [{members.Length}/{Protocol.MaxPeople}]";
        foreach (var peer in members.Where(p => !previous.ContainsKey(p.Id) && p.Id != _client.Id)) AppendNotice(peer.Name + " в комнате.");
        foreach (var peer in previous.Values.Where(p => members.All(m => m.Id != p.Id))) AppendNotice(peer.Name + " вышел из комнаты.");
    }

    private ContextMenuStrip BuildPeopleMenu()
    {
        var menu = new ContextMenuStrip();
        var mute = new ToolStripMenuItem("Не слышать участника");
        mute.Click += (_, _) =>
        {
            if (SelectedPeer() is not { } peer) return;
            if (!_localMutes.Add(peer.Id)) _localMutes.Remove(peer.Id);
            _audio?.Mixer.SetUserVolume(peer.Id, _localMutes.Contains(peer.Id) ? 0 : _userVolumes.GetValueOrDefault(peer.Id, 1));
        };
        menu.Items.Add(mute);
        foreach (var volume in new[] { 50, 100, 150 })
        {
            var item = new ToolStripMenuItem("Громкость " + volume + "%");
            item.Click += (_, _) => { if (SelectedPeer() is { } peer) { _localMutes.Remove(peer.Id); _userVolumes[peer.Id] = volume / 100f; _audio?.Mixer.SetUserVolume(peer.Id, volume / 100f); } };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var kick = new ToolStripMenuItem("Отключить от комнаты");
        kick.Click += (_, _) => { if (SelectedPeer() is { } peer && _server != null && peer.Id != _client?.Id) _server.Kick(peer.Id); };
        menu.Items.Add(kick);
        menu.Opening += (_, e) =>
        {
            var peer = SelectedPeer(); if (peer == null || peer.Id == _client?.Id) { e.Cancel = true; return; }
            mute.Checked = _localMutes.Contains(peer.Id); kick.Enabled = _server != null;
        };
        return menu;
    }
    private PeerInfo? SelectedPeer() => _people.SelectedItems.Count == 1 ? _people.SelectedItems[0].Tag as PeerInfo : null;

    private void AppendChat(ChatEntry chat)
    {
        var prefix = $"[{chat.Time.LocalDateTime:HH:mm:ss}] {chat.Name}: ";
        _chat.AddMessage(chat, chat.Sender == _client?.Id);
        if (chat.Image != null && _client?.GetImage(chat.Image.Id) is { } image) _ = _chat.SetImageAsync(image);
        RememberText(prefix + chat.Text + Environment.NewLine);
    }
    private void AppendNotice(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] * {text}{Environment.NewLine}";
        _chat.AddNotice(line); RememberText(line);
    }
    private void RememberText(string text)
    { _transcript.Append(text); if (_transcript.Length > 1000000) _transcript.Remove(0, 250000); }
    private void ExportChat()
    {
        using var save = new SaveFileDialog { Filter = "Текстовый файл (*.txt)|*.txt", FileName = $"2WChat-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", Title = "Сохранить переписку" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(save.FileName, _transcript.ToString(), new UTF8Encoding(true)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowError(e.Message); }
    }

    private static string[] Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{n.Name}: {a.Address}")).ToArray();
    }
    private void ShowAddresses()
    {
        using var dialog = new Form { Text = "2WChat — адреса этого компьютера", Font = Font, ClientSize = new Size(620, 350), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Margin = new Padding(12) };
        text.Text = "Передайте друзьям адрес адаптера Radmin VPN (или вашей LAN/VPN).\r\nПорт комнаты: " + (int)_port.Value + " TCP и UDP.\r\n\r\n" + string.Join("\r\n", Addresses()) +
            (_server == null ? "" : "\r\n\r\nОтпечаток сервера (SHA-256):\r\n" + ServerIdentity.FormatFingerprint(_server.Fingerprint)) + "\r\n\r\n127.0.0.1 работает только на этом же компьютере.";
        var close = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
        dialog.Controls.Add(text); dialog.Controls.Add(close); dialog.AcceptButton = close; dialog.ShowDialog(this);
    }
    private void ShowHelp() => MessageBox.Show(this,
        "1. Все участники входят в одну сеть Radmin VPN, ZeroTier, Hamachi или LAN.\n\n" +
        "2. Владелец вводит имя и пароль (по желанию), затем нажимает «Создать комнату».\n\n" +
        "3. Остальные вводят его VPN-IP, тот же порт и пароль, затем нажимают «Подключиться». Сверьте отпечаток новой комнаты с владельцем.\n\n" +
        "4. Если Windows спросит о сети, разрешите доступ для доверенной VPN-сети. По умолчанию нужны входящие TCP 48500 и UDP 48500 у владельца. В комплекте есть Allow-Firewall.cmd.\n\n" +
        "F6 — микрофон. F7 — весь звук и передача микрофона.\nEnter — сообщение; Shift+Enter — новая строка.\nПравый клик по участнику — его громкость / отключение.\nПри закрытии программы владельца комната завершается.\n\n" +
        "Если сообщения идут, а UDP недоступен — проверьте UDP-порт и VPN. Если есть эхо — используйте наушники.", "Как подключиться", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
