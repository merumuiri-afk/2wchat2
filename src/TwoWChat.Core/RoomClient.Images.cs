using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TwoWChat.Core;

public sealed partial class RoomClient
{
    private readonly object _imageGate = new();
    private readonly ImageCache _imageCache = new();
    private readonly Channel<ImageInfo> _imageRequests = Channel.CreateBounded<ImageInfo>(128);
    private readonly ConcurrentDictionary<Guid, byte> _requestedImages = new();
    private readonly SemaphoreSlim _uploadSerial = new(1, 1);
    private volatile UploadRequest? _upload;
    private volatile DownloadRequest? _download;
    public event Action<ImagePayload>? ImageReceived;
    public event Action<Guid, string>? ImageUnavailable;

    public ImagePayload? GetImage(Guid id) { lock (_imageGate) return _imageCache.Get(id); }

    public void RequestImage(ImageInfo info)
    {
        if (!SupportsImages || !Connected) return;
        ImageAttachments.ValidateInfo(info);
        var cached = GetImage(info.Id);
        if (cached != null) { ImageReceived?.Invoke(cached); return; }
        if (!_requestedImages.TryAdd(info.Id, 0)) return;
        if (!_imageRequests.Writer.TryWrite(info))
        { _requestedImages.TryRemove(info.Id, out _); ImageUnavailable?.Invoke(info.Id, "Очередь изображений занята. Нажмите для повторной загрузки."); }
    }

    public async Task<ChatEntry> SendImageAsync(ImagePayload image, string caption = "", IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!SupportsImages) throw new InvalidOperationException("Программа владельца комнаты не поддерживает картинки. Обновите её до версии 1.2.");
        if (image.Info.ContentType == "image/gif" && !SupportsAnimatedGifs) throw new InvalidOperationException("Для GIF обновите программу владельца комнаты до 1.7.");
        if (image.Info.IsSticker && image.Info.ContentType == "image/gif" && !SupportsAnimatedStickers) throw new InvalidOperationException("Для GIF-стикеров обновите программу владельца комнаты до 1.11.");
        ImageAttachments.Validate(image); caption = Protocol.CleanText(caption);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _uploadSerial.WaitAsync(stop.Token);
        var request = new UploadRequest(Guid.NewGuid()); _upload = request;
        try
        {
            var info = image.Info with { Id = request.Id };
            progress?.Report(0);
            await SendAsync(new WireMessage("image_start") { Id = request.Id, Image = info, Text = caption }, stop.Token);
            var ready = await request.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            if (ready.Type == "image_error") throw new InvalidDataException(ready.Text);
            for (var offset = 0; offset < image.Data.Length;)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (request.Done.Task.IsCompleted && (await request.Done.Task).Type == "image_error") throw new InvalidDataException((await request.Done.Task).Text);
                var count = Math.Min(Protocol.ImageChunkBytes, image.Data.Length - offset);
                await SendAsync(new WireMessage("image_chunk") { Id = request.Id, Offset = offset, Data = image.Data.AsSpan(offset, count).ToArray() }, stop.Token);
                offset += count; progress?.Report(Math.Min(99, (int)(offset * 100L / image.Data.Length)));
                if (offset % (Protocol.ImageChunkBytes * 4) == 0) await Task.Yield();
            }
            await SendAsync(new WireMessage("image_finish") { Id = request.Id }, stop.Token);
            var done = await request.Done.Task.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            if (done.Type != "image_sent" || done.Chat == null) throw new InvalidDataException(done.Text ?? "Изображение не отправлено.");
            progress?.Report(100); return done.Chat;
        }
        finally
        {
            _upload = null;
            if (Connected)
                try { await SendAsync(new WireMessage("image_cancel") { Id = request.Id }); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            _uploadSerial.Release();
        }
    }

    private async Task DownloadImagesAsync()
    {
        try
        {
            await foreach (var info in _imageRequests.Reader.ReadAllAsync(_stop.Token))
            {
                if (GetImage(info.Id) != null) { _requestedImages.TryRemove(info.Id, out _); continue; }
                var request = new DownloadRequest(info); _download = request;
                try
                {
                    await SendAsync(new WireMessage("image_get") { Id = info.Id });
                    var error = await request.Done.Task.WaitAsync(TimeSpan.FromMinutes(2), _stop.Token);
                    if (error != null) ImageUnavailable?.Invoke(info.Id, error);
                }
                catch (TimeoutException) { ImageUnavailable?.Invoke(info.Id, "Не удалось загрузить изображение. Нажмите для повтора."); }
                finally { _download = null; _requestedImages.TryRemove(info.Id, out _); }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private bool ReadImageMessage(WireMessage message)
    {
        if (message.Type is "image_ready" or "image_sent" or "image_error")
        {
            var upload = _upload;
            if (upload?.Id == message.Id)
            {
                if (message.Type is "image_ready" or "image_error") upload.Ready.TrySetResult(message);
                if (message.Type is "image_sent" or "image_error") upload.Done.TrySetResult(message);
            }
            else if (message.Type == "image_error" && _download?.Info.Id == message.Id) _download.Done.TrySetResult(message.Text ?? "Изображение недоступно.");
            return true;
        }
        if (message.Type is not ("image_start" or "image_chunk" or "image_finish")) return false;
        var download = _download;
        if (download == null || download.Info.Id != message.Id || download.Done.Task.IsCompleted) return true;
        try
        {
            switch (message.Type)
            {
                case "image_start":
                    if (message.Image != download.Info) throw new InvalidDataException("Описание изображения не совпадает с сообщением.");
                    download.Buffer = new ImageTransferBuffer(download.Info);
                    break;
                case "image_chunk":
                    if (download.Buffer == null) throw new InvalidDataException("Изображение не начало загружаться.");
                    download.Buffer.Append(message.Offset, message.Data);
                    break;
                case "image_finish":
                    var image = download.Buffer?.Complete() ?? throw new InvalidDataException("Изображение не начало загружаться.");
                    lock (_imageGate) _imageCache.Add(image);
                    ImageReceived?.Invoke(image); download.Done.TrySetResult(null);
                    break;
            }
        }
        catch (InvalidDataException e) { download.Done.TrySetResult(e.Message); }
        return true;
    }

    private sealed class UploadRequest(Guid id)
    {
        public Guid Id { get; } = id;
        public TaskCompletionSource<WireMessage> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WireMessage> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class DownloadRequest(ImageInfo info)
    {
        public ImageInfo Info { get; } = info;
        public ImageTransferBuffer? Buffer;
        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
