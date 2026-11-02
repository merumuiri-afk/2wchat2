using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TwoWChat.Core;

public sealed partial class RoomClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<VoiceFrame> _voiceOut = Channel.CreateBounded<VoiceFrame>(new BoundedChannelOptions(5) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private UdpClient? _udp;
    private UdpCipher? _cipher;
    private Task[] _tasks = [];
    private int _closed, _disposed, _ready;
    private long _lastUdp, _lastPong, _voiceSent, _voiceReceived;
    private PeerInfo[] _members = [];
    public Guid Id { get; private set; }
    public string Room { get; private set; } = "";
    public ChatEntry[] History { get; private set; } = [];
    public bool SupportsImages { get; private set; }
    public bool SupportsAvatars { get; private set; }
    public bool SupportsAnimatedGifs { get; private set; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _avatars = new();
    public byte[]? GetAvatar(Guid id) => _avatars.GetValueOrDefault(id);
    public Task SetAvatarAsync(byte[] data)
    {
        if (!AvatarData.IsValid(data)) throw new InvalidDataException("Некорректная аватарка.");
        return SupportsAvatars ? SendAsync(new WireMessage("avatar") { Data = data }) : Task.CompletedTask;
    }
    public PeerInfo[] Members => Volatile.Read(ref _members);
    public bool Connected => Volatile.Read(ref _ready) == 1 && Volatile.Read(ref _closed) == 0;
    public bool UdpReady => Interlocked.Read(ref _lastUdp) != 0 && Environment.TickCount64 - Interlocked.Read(ref _lastUdp) < 7000;
    public int PingMs { get; private set; }
    public long VoicePacketsSent => Interlocked.Read(ref _voiceSent);
    public long VoicePacketsReceived => Interlocked.Read(ref _voiceReceived);
    public event Action<WireMessage>? Message;
    public event Action<VoiceFrame>? Voice;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, string name, string password, bool micMuted, bool deafened,
        Func<string, bool> acceptFingerprint, CancellationToken cancellationToken = default)
    {
        if (_tcp != null) throw new InvalidOperationException("Клиент уже использован.");
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var addresses = await Dns.GetHostAddressesAsync(host.Trim(), AddressFamily.InterNetwork, timeout.Token);
        if (addresses.Length == 0) throw new IOException("IPv4-адрес не найден.");
        _tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await _tcp.ConnectAsync(addresses, port, connectTimeout.Token);
        }
        var remote = (IPEndPoint)_tcp.Client.RemoteEndPoint!;
        _ssl = new SslStream(_tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert == null) return false;
            var fingerprint = ServerIdentity.Fingerprint(cert);
            if (!acceptFingerprint(fingerprint)) return false;
            _screenFingerprint = fingerprint; return true;
        });
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "2WChat",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
        }, timeout.Token);
        await Protocol.WriteAsync(_ssl, new WireMessage("hello")
        { Name = name, Password = password, MicMuted = micMuted, Deafened = deafened, SupportsImages = true, SupportsScreenShare = true, SupportsAvatars = true, SupportsAnimatedGifs = true }, timeout.Token);
        var welcome = await Protocol.ReadAsync(_ssl, timeout.Token) ?? throw new IOException("Сервер закрыл соединение.");
        if (welcome.Type == "error") throw new IOException(welcome.Text ?? "Подключение отклонено.");
        if (welcome.Type != "welcome" || welcome.Version != Protocol.Version || welcome.Key == null || welcome.Id == Guid.Empty || welcome.Token == Guid.Empty)
            throw new InvalidDataException("Некорректный ответ сервера.");
        Id = welcome.Id;
        Room = welcome.Room ?? "Общая комната";
        History = welcome.History ?? [];
        SupportsImages = welcome.SupportsImages;
        SupportsAvatars = welcome.SupportsAvatars;
        SupportsAnimatedGifs = welcome.SupportsAnimatedGifs;
        InitializeScreenTransport(welcome, remote);
        Volatile.Write(ref _members, welcome.Members ?? []);
        var key = Convert.FromBase64String(welcome.Key);
        _cipher = new UdpCipher(welcome.Token, key, true);
        CryptographicOperations.ZeroMemory(key);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ReceiveBufferSize = 262144;
        _udp.Connect(remote);
        _lastPong = Environment.TickCount64;
        Volatile.Write(ref _ready, 1);
        _tasks = [ReadLoopAsync(), UdpLoopAsync(), VoiceSendLoopAsync(), HeartbeatLoopAsync(), DownloadImagesAsync()];
        foreach (var chat in History) if (chat.Image != null) RequestImage(chat.Image);
    }

    public Task SendChatAsync(string text) => SendAsync(new WireMessage("chat") { Text = Protocol.CleanText(text) });
    public Task SetStateAsync(bool micMuted, bool deafened) => SendAsync(new WireMessage("state") { MicMuted = micMuted, Deafened = deafened });
    public bool SendVoice(uint sequence, byte level, byte[] opus) => Connected && _voiceOut.Writer.TryWrite(new VoiceFrame(Id, sequence, level, opus));

    public async Task SendAsync(WireMessage message, CancellationToken cancellationToken = default)
    {
        if (!Connected) throw new IOException("Нет подключения к комнате.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await _writeLock.WaitAsync(timeout.Token);
        try { await Protocol.WriteAsync(_ssl!, message, timeout.Token); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { Close("Соединение с сервером потеряно."); throw; }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await Protocol.ReadAsync(_ssl!, _stop.Token);
                if (message == null) break;
                if (ReadImageMessage(message)) continue;
                if (message.Type == "screens" && !UpdateScreens(message.Screens ?? [], message.Stamp)) continue;
                if (message.Type == "members")
                {
                    Volatile.Write(ref _members, message.Members ?? []);
                    foreach (var id in _avatars.Keys) if (!_members.Any(m => m.Id == id)) _avatars.TryRemove(id, out _);
                }
                if (message.Type == "avatar")
                {
                    if (!SupportsAvatars || message.Data == null || !AvatarData.IsValid(message.Data) || !Members.Any(m => m.Id == message.Id)) continue;
                    if (message.Data.Length == 0) _avatars.TryRemove(message.Id, out _);
                    else _avatars[message.Id] = message.Data;
                }
                if (message.Type == "pong")
                {
                    _lastPong = Environment.TickCount64;
                    PingMs = (int)Math.Clamp(Environment.TickCount64 - message.Stamp, 0, 60000);
                }
                if (message.Type == "room_name") Room = Protocol.CleanName(message.Room, 40);
                Message?.Invoke(message);
                if (message.Chat?.Image != null) RequestImage(message.Chat.Image);
            }
            Close("Сервер завершил соединение. Можно подключиться снова.");
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        { Close("Соединение с сервером потеряно. Проверьте VPN и подключитесь снова."); }
    }

    private async Task UdpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await _udp!.ReceiveAsync(_stop.Token); }
                catch (SocketException) { await Task.Delay(100, _stop.Token); continue; }
                if (!_cipher!.TryOpen(result.Buffer, out var kind, out var plain)) continue;
                Interlocked.Exchange(ref _lastUdp, Environment.TickCount64);
                if (kind != 1) continue;
                var voice = UdpCipher.UnpackVoice(plain);
                if (voice == null || voice.Speaker == Id || !Members.Any(p => p.Id == voice.Speaker)) continue;
                Interlocked.Increment(ref _voiceReceived);
                Voice?.Invoke(voice);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task VoiceSendLoopAsync()
    {
        try
        {
            await foreach (var frame in _voiceOut.Reader.ReadAllAsync(_stop.Token))
            {
                var data = _cipher!.Seal(1, UdpCipher.PackVoice(frame));
                try
                {
                    await _udp!.SendAsync(data, _stop.Token);
                    Interlocked.Increment(ref _voiceSent);
                }
                catch (SocketException) { }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                await SendAsync(new WireMessage("ping") { Stamp = Environment.TickCount64 });
                var data = _cipher!.Seal(0, []);
                try { await _udp!.SendAsync(data, _stop.Token); } catch (SocketException) { }
                if (Environment.TickCount64 - Interlocked.Read(ref _lastPong) > 14000)
                { Close("Сервер не отвечает. Проверьте VPN и подключитесь снова."); return; }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _tcp?.Dispose();
        _udp?.Dispose();
        _voiceOut.Writer.TryComplete();
        _imageRequests.Writer.TryComplete();
        CloseScreenMedia();
        Disconnected?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Close("Отключено.");
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
        _ssl?.Dispose();
        _cipher?.Dispose();
        await DisposeScreenMediaAsync();
        _stop.Dispose();
        // Public sends may still be unwinding cancellation and releasing the lock.
        // It has no unmanaged wait handle; let it be collected with this client.
        lock (_imageGate) _imageCache.Clear();
    }
}
