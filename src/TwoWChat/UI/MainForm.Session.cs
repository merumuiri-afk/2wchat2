using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using TwoWChat.Audio;
using TwoWChat.Core;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private async Task ConnectRoomAsync(bool create, bool localOnly = false)
    {
        if (_busy || _client != null) return;
        var name = Protocol.CleanName(_name.Text);
        if (name.Length == 0) { ShowError("Введите имя участника."); _name.Focus(); return; }
        var host = _host.Text.Trim(); var port = (int)_port.Value;
        if (!create)
        {
            host = host.Replace("2wchat://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            var parts = host.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var specified) && specified is >= 1024 and <= 65535)
            { host = parts[0]; port = specified; _port.Value = port; _host.Text = host; }
            if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(':'))
            { ShowError("Введите IPv4-адрес владельца комнаты, например 26.1.2.3. Можно указать IP:порт."); return; }
        }
        _busy = true; _historyReady = false; _pendingChats.Clear();
        _connectStop = new CancellationTokenSource(); SetConnectedUi(false);
        _connectionStatus.Text = create ? "Создание комнаты…" : "Подключение…";
        try
        {
            if (create)
            {
                _identity = AppStore.Identity();
                _server = new RoomServer(_identity, _room.Text, _password.Text);
                _server.Notice += text => Post(() => AppendNotice(text));
                _server.Start(port, localOnly ? IPAddress.Loopback : IPAddress.Any);
                host = "127.0.0.1";
            }
            var client = new RoomClient(); _client = client;
            client.Message += message => Post(() => { if (_client == client) OnMessage(message); });
            client.Voice += frame => { if (_client == client) _audio?.AddVoice(frame); };
            client.ImageReceived += image => Post(async () => { if (_client == client) await _chat.SetImageAsync(image); });
            client.ImageUnavailable += (id, text) => Post(() => { if (_client == client) _chat.ImageFailed(id, text); });
            AttachScreenEvents(client);
            client.Disconnected += reason => Post(async () =>
            {
                if (_client != client || _stopping || _busy) return;
                AppendNotice(reason); await StopSessionAsync();
            });
            var endpoint = $"{host.ToLowerInvariant()}:{port}";
            var pinned = create ? _server!.Fingerprint : null;
            await client.ConnectAsync(host, port, name, _password.Text, _mute.Checked, _deafen.Checked,
                fingerprint => pinned != null ? fingerprint == pinned : AcceptServer(endpoint, fingerprint), _connectStop.Token);
            if (_connectStop.IsCancellationRequested) throw new OperationCanceledException();
            _settings.Name = name; _settings.Port = port; _settings.RoomName = _room.Text;
            if (!create) _settings.Host = host;
            _connectedAt = Environment.TickCount64;
            _roomLabel.Text = " " + client.Room;
            UpdateWindowCaption();
            AppendNotice(create ? $"Комната создана. Порт {port} TCP + UDP. Передайте друзьям свой VPN-IP (кнопка «Мой IP…»)." : $"Подключено к {host}:{port} — {client.Room}.");
            foreach (var chat in client.History) AppendChat(chat);
            var historyIds = client.History.Select(c => c.Id).ToHashSet();
            foreach (var chat in _pendingChats.Where(c => !historyIds.Contains(c.Id))) AppendChat(chat);
            _pendingChats.Clear(); _historyReady = true;
            RefreshMembers(); StartAudio(); SaveSettings();
        }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            var message = e is OperationCanceledException ? "Подключение отменено или истекло время ожидания." : FriendlyError(e);
            AppendNotice(message); AppStore.Log(e.GetType().Name + ": " + e.Message);
            await StopSessionAsync();
            if (e is not OperationCanceledException && _smokeDirectory == null) ShowError(message);
        }
        finally
        {
            _busy = false; _connectStop?.Dispose(); _connectStop = null;
            SetConnectedUi(_client?.Connected == true);
            if (_client?.Connected == true) _message.Focus();
            if (_closeRequested) Close();
        }
    }

    private bool AcceptServer(string endpoint, string fingerprint)
    {
        if (InvokeRequired) return (bool)Invoke(() => AcceptServer(endpoint, fingerprint));
        if (_closing || _connectStop?.IsCancellationRequested == true) return false;
        _settings.TrustedHosts.TryGetValue(endpoint, out var previous);
        if (string.Equals(previous, fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
        var changed = previous != null;
        var text = (changed ? "Сертификат этой комнаты изменился. Возможно, владелец переустановил программу. Сверьте отпечаток с владельцем перед подключением." : "Первое подключение к этой комнате. Сверьте отпечаток с владельцем через доверенный канал (у него: «Мой IP…»).") +
            $"\n\nАдрес: {endpoint}\n\nSHA-256:\n{ServerIdentity.FormatFingerprint(fingerprint)}\n\nДоверять этому серверу и продолжить?";
        var result = MessageBox.Show(this, text, changed ? "Изменился сертификат комнаты" : "Новая комната", MessageBoxButtons.YesNo, changed ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return false;
        _settings.TrustedHosts[endpoint] = fingerprint; SaveSettings(); return true;
    }

    private void StartAudio()
    {
        _audio?.Dispose();
        var audio = new AudioEngine(_settings.JitterMs); _audio = audio;
        audio.MicMuted = _mute.Checked; audio.Deafened = _deafen.Checked; audio.Mixer.Deafened = _deafen.Checked;
        audio.MicrophoneGain = _settings.InputGain / 100f; audio.Mixer.Volume = _settings.OutputVolume / 100f;
        audio.GateEnabled = _settings.NoiseGate; audio.GateDb = _settings.GateDb; audio.PushToTalk = _settings.PushToTalk;
        foreach (var (id, volume) in _userVolumes) audio.Mixer.SetUserVolume(id, volume);
        foreach (var id in _localMutes) audio.Mixer.SetUserVolume(id, 0);
        var client = _client;
        audio.Encoded += (sequence, level, opus) => { if (_audio == audio) client?.SendVoice(sequence, level, opus); };
        audio.Error += text => Post(() => { if (_audio == audio) AppendNotice(text); });
        var inputs = AudioEngine.InputDevices(); var outputs = AudioEngine.OutputDevices();
        var input = inputs.FirstOrDefault(d => d.Name == _settings.InputDevice)?.Number ?? -1;
        var output = outputs.FirstOrDefault(d => d.Name == _settings.OutputDevice)?.Number ?? -1;
        audio.Start(input, output, _settings.Bitrate);
        if (!audio.HasMicrophone) _mute.Checked = true;
        if (!audio.HasOutput) AppendNotice("Звуковой вывод недоступен. Можно продолжать текстовый чат или выбрать другое устройство в настройках.");
    }

    private async Task StopSessionAsync()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            _imageSendStop?.Cancel();
            await CloseScreenResourcesAsync();
            _audio?.Dispose(); _audio = null;
            var client = _client; _client = null;
            if (client != null) await client.DisposeAsync();
            var server = _server; _server = null;
            if (server != null) await server.DisposeAsync();
            _identity?.Dispose(); _identity = null;
            _people.Items.Clear(); _localMutes.Clear(); _userVolumes.Clear();
            _heading.Text = "  2WChat  /  голос и сообщения";
            _roomLabel.Text = " Общий чат";
            Text = "2WChat — голос и сообщения";
            SetConnectedUi(false);
        }
        finally { _stopping = false; }
    }

    private void SetConnectedUi(bool connected)
    {
        _notifications.Configure(_settings, connected);
        UpdateWindowCaption();
        foreach (var control in new Control[] { _name, _host, _password, _port, _room, _create, _join }) control.Enabled = !connected && !_busy;
        _leave.Enabled = connected || _busy;
        _send.Enabled = connected; _message.Enabled = connected;
        UpdateImageButton();
        UpdateScreenControls();
        _audioSettings.Enabled = !_busy;
        _connectionStatus.Text = connected ? (_server == null ? "Подключено" : "Вы — владелец комнаты") : _busy ? "Подключение…" : "Нет подключения";
        if (!connected) { _networkStatus.Text = "TCP + UDP / " + _settings.Port; _inputMeter.Value = 0; }
        UpdateAudioButtons();
    }

    private async Task ChangeStateAsync()
    {
        _settings.MicMuted = _mute.Checked; _settings.Deafened = _deafen.Checked;
        _notifications.Configure(_settings, _client?.Connected == true);
        if (_audio != null)
        { _audio.MicMuted = _mute.Checked; _audio.Deafened = _deafen.Checked; _audio.Mixer.Deafened = _deafen.Checked; }
        UpdateAudioButtons(); SaveSettings();
        if (_client?.Connected == true)
        {
            try { await _client.SetStateAsync(_mute.Checked, _deafen.Checked); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { AppendNotice("Не удалось обновить состояние: " + e.Message); }
        }
    }

    private async Task SendChatAsync()
    {
        if (_client?.Connected != true || string.IsNullOrWhiteSpace(_message.Text)) return;
        var text = _message.Text; _send.Enabled = false;
        try { await _client.SendChatAsync(text); if (_message.Text == text) _message.Clear(); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { AppendNotice("Сообщение не отправлено: " + e.Message); }
        finally { _send.Enabled = _client?.Connected == true; _message.Focus(); }
    }

    private void OnMessage(WireMessage message)
    {
        switch (message.Type)
        {
            case "chat" when message.Chat != null:
                if (!_historyReady) _pendingChats.Add(message.Chat);
                else
                {
                    AppendChat(message.Chat);
                    _notifications.Notify(message.Chat, _client?.Id ?? Guid.Empty);
                }
                break;
            case "members": RefreshMembers(); break;
            case "notice": AppendNotice(message.Text ?? ""); break;
        }
    }

    private void OpenSettings()
    {
        if (_busy) return;
        using var form = new SettingsForm(_settings, _client?.Connected == true);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _settings = form.Result; SaveSettings(); UpdateAudioButtons();
        _notifications.Configure(_settings, _client?.Connected == true);
        if (_client?.Connected == true) StartAudio();
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _timer.Stop(); _connectStop?.Cancel();
        if (_busy) { _closeRequested = true; return; }
        _settings.Name = Protocol.CleanName(_name.Text); _settings.Host = _host.Text.Trim(); _settings.Port = (int)_port.Value; _settings.RoomName = _room.Text; SaveSettings();
        await StopSessionAsync(); _closing = true; Close();
    }

    private void SaveSettings()
    {
        try { AppStore.Save(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppStore.Log("Settings save: " + e.Message); }
    }
    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void ShowError(string message) => MessageBox.Show(this, message, "2WChat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private static string FriendlyError(Exception e) => e switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => "Этот порт уже занят. Выберите другой порт у всех участников комнаты.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostUnreachable } => "Сервер недоступен. Проверьте VPN-IP, порт и запущена ли комната. На компьютере владельца должны быть разрешены TCP и UDP на этом порту.",
        AuthenticationException => "Сертификат комнаты не принят или защищённое соединение не установлено.",
        _ => e.Message
    };
}
