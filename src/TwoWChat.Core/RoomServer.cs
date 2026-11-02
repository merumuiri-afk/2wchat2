using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace TwoWChat.Core;

public sealed partial class RoomServer : IAsyncDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly string _room;
    private readonly byte[] _passwordHash;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Peer> _peers = [];
    private readonly ConcurrentDictionary<Guid, Peer> _tokens = new();
    private readonly Queue<ChatEntry> _history = new();
    private readonly ImageCache _images = new();
    private int _uploadBytes;
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly SemaphoreSlim _handshakeSlots = new(24);
    private TcpListener? _listener;
    private UdpClient? _udp;
    private Task? _acceptTask, _udpTask, _sweepTask;
    private int _handlerId;
    private int _disposed;
    public int Port { get; private set; }
    public string Fingerprint => ServerIdentity.Fingerprint(_certificate);
    public event Action<string>? Notice;

    public RoomServer(X509Certificate2 certificate, string room, string password)
    {
        _certificate = certificate;
        _room = Protocol.CleanName(room, 40);
        if (_room.Length == 0) _room = "Общая комната";
        if (password.Length > 128) throw new ArgumentException("Пароль слишком длинный.");
        _passwordHash = HashPassword(password);
    }

    public void Start(int port = Protocol.DefaultPort, IPAddress? bind = null)
    {
        if (_listener != null) throw new InvalidOperationException("Сервер уже запущен.");
        if (port != 0 && port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        bind ??= IPAddress.Any;
        try
        {
            _listener = new TcpListener(bind, port);
            _listener.Start(32);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _udp = new UdpClient(new IPEndPoint(bind, Port));
            _udp.Client.ReceiveBufferSize = 262144;
            _udp.Client.SendBufferSize = 262144;
            _acceptTask = AcceptLoopAsync();
            _udpTask = UdpLoopAsync();
            _sweepTask = SweepLoopAsync();
        }
        catch
        {
            _listener?.Stop();
            _udp?.Dispose();
            _listener = null;
            throw;
        }
    }

    private byte[] HashPassword(string password)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(password);
        return HMACSHA256.HashData(_salt, input);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener!.AcceptTcpClientAsync(_stop.Token);
                if (!_handshakeSlots.Wait(0)) { tcp.Dispose(); continue; }
                var id = Interlocked.Increment(ref _handlerId);
                var task = HandleAsync(tcp);
                _handlers[id] = task;
                _ = task.ContinueWith(_ => _handlers.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        { if (!_stop.IsCancellationRequested) Notice?.Invoke("Сервер: " + e.Message); }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        Peer? peer = null;
        var slotHeld = true;
        using (tcp)
        using (var ssl = new SslStream(tcp.GetStream(), false))
        {
            try
            {
                tcp.NoDelay = true;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(60));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, handshake.Token);
                var hello = await Protocol.ReadAsync(ssl, handshake.Token);
                if (hello?.Type == "screen_media")
                {
                    _handshakeSlots.Release(); slotHeld = false;
                    await HandleScreenMediaAsync(tcp, ssl, hello, handshake.Token);
                    return;
                }
                string? error = null;
                var name = Protocol.CleanName(hello?.Name);
                if (hello is not { Type: "hello", Version: Protocol.Version }) error = "Несовместимая версия 2WChat.";
                else if (name.Length == 0) error = "Укажите имя участника.";
                else if ((hello.Password?.Length ?? 0) > 128 || !CryptographicOperations.FixedTimeEquals(_passwordHash, HashPassword(hello.Password ?? ""))) error = "Неверный пароль комнаты.";
                if (error == null)
                {
                    lock (_gate)
                    {
                        if (_peers.Count >= Protocol.MaxPeople) error = "Комната заполнена (16 участников).";
                        else if (_peers.Values.Any(p => string.Equals(p.Info.Name, name, StringComparison.OrdinalIgnoreCase))) error = "Это имя уже занято. Выберите другое.";
                        else
                        {
                            peer = new Peer(tcp, ssl, name, hello!.MicMuted, hello.Deafened, hello.SupportsImages, hello.SupportsScreenShare);
                            _peers.Add(peer.Info.Id, peer);
                            _tokens[peer.Token] = peer;
                            peer.Queue(new WireMessage("welcome")
                            {
                                Id = peer.Info.Id, Token = peer.Token, Key = Convert.ToBase64String(peer.Key), Room = _room,
                                Members = _peers.Values.Select(p => p.Info).ToArray(), History = _history.ToArray(), SupportsImages = true,
                                SupportsScreenShare = true, ScreenKey = Convert.ToBase64String(peer.ScreenKey), Screens = ScreenSnapshot(), Stamp = _screenRevision
                            });
                            BroadcastMembers();
                        }
                    }
                }
                if (error != null)
                {
                    await Protocol.WriteAsync(ssl, new WireMessage("error") { Text = error }, handshake.Token);
                    return;
                }
                _handshakeSlots.Release();
                slotHeld = false;
                var active = peer!;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, active.Stop.Token);
                var writer = WriteLoopAsync(active, linked.Token);
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        var message = await Protocol.ReadAsync(ssl, linked.Token);
                        if (message == null) break;
                        active.LastSeen = Environment.TickCount64;
                        switch (message.Type)
                        {
                            case "ping":
                                active.Queue(new WireMessage("pong") { Stamp = message.Stamp });
                                break;
                            case "state":
                                if (!active.ControlRate.Take()) break;
                                lock (_gate)
                                {
                                    active.Info = active.Info with { MicMuted = message.MicMuted, Deafened = message.Deafened };
                                    BroadcastMembers();
                                }
                                break;
                            case "chat":
                                if (!active.ChatRate.Take())
                                { active.Queue(new WireMessage("notice") { Text = "Слишком много сообщений. Подождите несколько секунд." }); break; }
                                var text = Protocol.CleanText(message.Text);
                                if (text.Length == 0) break;
                                lock (_gate)
                                {
                                    var chat = new ChatEntry(Guid.NewGuid(), active.Info.Id, active.Info.Name, text, DateTimeOffset.UtcNow);
                                    AddChat(chat);
                                }
                                break;
                            case "image_start": case "image_chunk": case "image_finish": case "image_cancel": case "image_get":
                                HandleImage(active, message);
                                break;
                            default: throw new InvalidDataException("Неизвестная команда.");
                        }
                    }
                }
                finally
                {
                    active.Stop.Cancel();
                    active.Outgoing.Writer.TryComplete();
                    try { await writer; } catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or AuthenticationException or System.Text.Json.JsonException or InvalidDataException)
            {
                if (e is AuthenticationException && !_stop.IsCancellationRequested) Notice?.Invoke("TLS: " + e.Message);
                // An untrusted or disconnected client must not stop the room.
            }
            finally
            {
                if (slotHeld) _handshakeSlots.Release();
                if (peer != null)
                {
                    lock (_gate)
                    {
                        _peers.Remove(peer.Info.Id);
                        _tokens.TryRemove(peer.Token, out _);
                        ClosePeerScreenMedia(peer);
                        ClearUpload(peer);
                        BroadcastMembers();
                    }
                    // The UDP loop can have a reference; Peer serializes crypto disposal.
                    peer.Dispose();
                }
            }
        }
    }

    private async Task WriteLoopAsync(Peer peer, CancellationToken ct)
    {
        ImagePayload? image = null;
        var offset = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                WireMessage message;
                // Chat, state changes and heartbeat always get priority over image chunks.
                if (peer.Outgoing.Reader.TryRead(out var control)) message = control;
                else if (image != null)
                {
                    if (offset < image.Data.Length)
                    {
                        var length = Math.Min(Protocol.ImageChunkBytes, image.Data.Length - offset);
                        message = new WireMessage("image_chunk") { Id = image.Info.Id, Offset = offset, Data = image.Data.AsSpan(offset, length).ToArray() };
                        offset += length;
                    }
                    else { message = new WireMessage("image_finish") { Id = image.Info.Id }; image = null; }
                }
                else if (peer.Images.Reader.TryRead(out var id))
                {
                    lock (_gate) image = _images.Get(id);
                    offset = 0;
                    message = image == null ? ImageError(id, "Изображение больше не хранится в комнате.") : new WireMessage("image_start") { Id = id, Image = image.Info };
                }
                else { await peer.SendSignal.WaitAsync(ct); continue; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await Protocol.WriteAsync(peer.Stream, message, timeout.Token);
            }
        }
        finally { peer.Stop.Cancel(); peer.Tcp.Close(); }
    }

    private void AddChat(ChatEntry chat)
    {
        _history.Enqueue(chat);
        var keep = _history.Where(c => c.Image == null || _images.Get(c.Image.Id) != null).ToArray();
        _history.Clear(); foreach (var item in keep) _history.Enqueue(item);
        while (_history.Count > 60) _history.Dequeue();
        while (_history.Count > 0 && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_history.ToArray(), Protocol.Json).Length > 22000) _history.Dequeue();
        Broadcast(new WireMessage("chat") { Chat = chat });
    }

    private static WireMessage ImageError(Guid id, string text) => new("image_error") { Id = id, Text = text };
    private void ClearUpload(Peer peer)
    {
        if (peer.Upload != null) { _uploadBytes -= peer.Upload.Info.Length; peer.Upload = null; }
    }

    private void HandleImage(Peer peer, WireMessage message)
    {
        lock (_gate)
        {
            try
            {
                if (!peer.SupportsImages) throw new InvalidDataException("Обновите программу для отправки изображений.");
                switch (message.Type)
                {
                    case "image_start":
                        ClearUpload(peer);
                        if (!peer.ImageRate.Take()) throw new InvalidDataException("Подождите несколько секунд перед отправкой следующего изображения.");
                        ImageAttachments.ValidateInfo(message.Image);
                        if (message.Id != message.Image!.Id) throw new InvalidDataException("Неверный номер передачи.");
                        if (_uploadBytes + message.Image.Length > Protocol.ImageCacheBytes) throw new InvalidDataException("В комнате уже передаются изображения. Попробуйте чуть позже.");
                        var caption = Protocol.CleanText(message.Text);
                        peer.Upload = new ImageTransferBuffer(message.Image, caption); _uploadBytes += message.Image.Length;
                        peer.Queue(new WireMessage("image_ready") { Id = message.Id });
                        break;
                    case "image_chunk":
                        if (peer.Upload == null || peer.Upload.Info.Id != message.Id) throw new InvalidDataException("Передача изображения не начата.");
                        peer.Upload.Append(message.Offset, message.Data);
                        break;
                    case "image_finish":
                        if (peer.Upload == null || peer.Upload.Info.Id != message.Id) throw new InvalidDataException("Передача изображения не начата.");
                        var uploaded = peer.Upload.Complete();
                        var info = uploaded.Info with { Id = Guid.NewGuid(), FileName = ImageAttachments.SafeFileName(uploaded.Info.FileName, uploaded.Info.ContentType) };
                        var stored = new ImagePayload(info, uploaded.Data);
                        _images.Add(stored);
                        var text = peer.Upload.Caption.Length == 0 ? "Изображение: " + info.FileName : peer.Upload.Caption;
                        var chat = new ChatEntry(Guid.NewGuid(), peer.Info.Id, peer.Info.Name, text, DateTimeOffset.UtcNow) { Image = info };
                        ClearUpload(peer); AddChat(chat);
                        peer.Queue(new WireMessage("image_sent") { Id = message.Id, Chat = chat });
                        break;
                    case "image_cancel":
                        if (peer.Upload?.Info.Id == message.Id) ClearUpload(peer);
                        break;
                    case "image_get":
                        if (!peer.ImageGetRate.Take()) throw new InvalidDataException("Слишком много запросов изображений.");
                        if (_images.Get(message.Id) == null) throw new InvalidDataException("Изображение больше не хранится в комнате.");
                        if (!peer.Images.Writer.TryWrite(message.Id)) throw new InvalidDataException("Очередь изображений занята. Повторите попытку.");
                        peer.Signal();
                        break;
                }
            }
            catch (InvalidDataException e)
            {
                if (message.Type is "image_start" or "image_chunk" or "image_finish") ClearUpload(peer);
                peer.Queue(ImageError(message.Id, e.Message));
            }
        }
    }

    private void Broadcast(WireMessage message)
    {
        foreach (var peer in _peers.Values) peer.Queue(message);
    }

    private void BroadcastMembers() => Broadcast(new WireMessage("members") { Members = _peers.Values.Select(p => p.Info).ToArray() });

    private async Task UdpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _udp!.ReceiveAsync(_stop.Token);
                if (!UdpCipher.TryGetToken(received.Buffer, out var token) || !_tokens.TryGetValue(token, out var sender)) continue;
                if (!sender.TryOpen(received.Buffer, out var kind, out var plain)) continue;
                sender.Endpoint = received.RemoteEndPoint;
                if (kind == 0 && plain.Length == 0)
                {
                    var ack = sender.Seal(0, []);
                    if (ack != null) await SendUdpAsync(ack, received.RemoteEndPoint);
                    continue;
                }
                if (kind != 1 || sender.Info.MicMuted || sender.Info.Deafened || !sender.VoiceRate.Take()) continue;
                var voice = UdpCipher.UnpackVoice(plain);
                if (voice == null) continue;
                var relay = UdpCipher.PackVoice(voice with { Speaker = sender.Info.Id });
                foreach (var recipient in _tokens.Values)
                {
                    var target = recipient.Endpoint;
                    if (recipient == sender || recipient.Info.Deafened || target == null) continue;
                    var data = recipient.Seal(1, relay);
                    if (data != null) await SendUdpAsync(data, target);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        catch (SocketException e) { if (!_stop.IsCancellationRequested) Notice?.Invoke("UDP: " + e.Message); }
    }

    private async Task SendUdpAsync(byte[] data, IPEndPoint target)
    {
        try { await _udp!.SendAsync(data, target, _stop.Token); }
        catch (SocketException) { /* Loss of a UDP recipient is not fatal. */ }
    }

    private async Task SweepLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                lock (_gate)
                    foreach (var peer in _peers.Values)
                    {
                        if (Environment.TickCount64 - peer.LastSeen > 20000) peer.Tcp.Close();
                        if (peer.Upload != null && Environment.TickCount64 - peer.Upload.LastActivity > 30000)
                        { var id = peer.Upload.Info.Id; ClearUpload(peer); peer.Queue(ImageError(id, "Истекло время передачи изображения.")); }
                    }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Kick(Guid id)
    {
        lock (_gate)
            if (_peers.TryGetValue(id, out var peer)) peer.Tcp.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener?.Stop();
        _udp?.Dispose();
        lock (_gate) foreach (var peer in _peers.Values) peer.Tcp.Close();
        var loops = new[] { _acceptTask, _udpTask, _sweepTask }.Where(t => t != null).Cast<Task>().ToArray();
        try { await Task.WhenAll(loops); } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        try { await Task.WhenAll(_handlers.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        _stop.Dispose();
        _handshakeSlots.Dispose();
        CryptographicOperations.ZeroMemory(_passwordHash);
    }

    private sealed class Peer : IDisposable
    {
        public PeerInfo Info;
        public readonly TcpClient Tcp;
        public readonly SslStream Stream;
        public readonly Guid Token = Guid.NewGuid();
        public readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        public readonly UdpCipher Cipher;
        public readonly CancellationTokenSource Stop = new();
        public readonly Channel<WireMessage> Outgoing = Channel.CreateBounded<WireMessage>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public readonly RateLimit ChatRate = new(2, 8);
        public readonly RateLimit ControlRate = new(5, 10);
        public readonly RateLimit VoiceRate = new(60, 20);
        public readonly RateLimit ImageRate = new(.5, 2);
        public readonly RateLimit ImageGetRate = new(10, 60);
        public readonly Channel<Guid> Images = Channel.CreateBounded<Guid>(4);
        public readonly SemaphoreSlim SendSignal = new(0, 1);
        public readonly bool SupportsImages;
        public readonly bool SupportsScreenShare;
        public readonly byte[] ScreenKey = RandomNumberGenerator.GetBytes(32);
        public readonly HashSet<TcpClient> ScreenConnections = [];
        public TcpClient? ScreenWatch;
        public ImageTransferBuffer? Upload;
        public IPEndPoint? Endpoint;
        public long LastSeen = Environment.TickCount64;
        private readonly object _cryptoGate = new();
        private bool _closed;

        public Peer(TcpClient tcp, SslStream stream, string name, bool muted, bool deafened, bool supportsImages, bool supportsScreenShare)
        {
            Tcp = tcp; Stream = stream; SupportsImages = supportsImages; SupportsScreenShare = supportsScreenShare;
            Info = new PeerInfo(Guid.NewGuid(), name, muted, deafened);
            Cipher = new UdpCipher(Token, Key, false);
        }
        public void Queue(WireMessage message) { if (!Outgoing.Writer.TryWrite(message)) Tcp.Close(); else Signal(); }
        public void Signal() { try { SendSignal.Release(); } catch (SemaphoreFullException) { } }
        public bool TryOpen(byte[] data, out byte kind, out byte[] plain)
        {
            lock (_cryptoGate)
            {
                kind = 0; plain = [];
                return !_closed && Cipher.TryOpen(data, out kind, out plain);
            }
        }
        public byte[]? Seal(byte kind, byte[] plain) { lock (_cryptoGate) return _closed ? null : Cipher.Seal(kind, plain); }
        public void Dispose()
        {
            lock (_cryptoGate)
            {
                _closed = true;
                Cipher.Dispose();
                CryptographicOperations.ZeroMemory(Key);
                CryptographicOperations.ZeroMemory(ScreenKey);
            }
            Stop.Dispose();
            SendSignal.Dispose();
        }
    }
}
