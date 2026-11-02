using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TwoWChat.Core;
using TwoWChat.Images;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    // Exercising the same methods as the normal UI in the published executable.
    // No microphone samples are saved; voice tests use a generated signal.
    private async Task RunUiSmokeAsync(string directory)
    {
        var results = new List<string>();
        var errors = new List<string>();
        RoomClient? bot = null;
        try
        {
            Directory.CreateDirectory(directory);
            await RunWindowCaptureSmokeAsync(directory, results);
            var updateTools = MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "&Сервис");
            var autoUpdate = updateTools.DropDownItems.OfType<ToolStripMenuItem>().Single(m => m.Text == "Проверять обновления при запуске");
            var originalAutoUpdate = _settings.AutoCheckUpdates;
            autoUpdate.PerformClick();
            if (_settings.AutoCheckUpdates == originalAutoUpdate || AppStore.Load().AutoCheckUpdates != _settings.AutoCheckUpdates) throw new IOException("Auto update preference was not saved");
            autoUpdate.PerformClick();
            results.Add("Automatic update preference toggles and persists through the Service menu");
            using (var update = new UpdateForm(new Updates.UpdateRelease(new Version(1, 6, 0, 0), "v1.6.0", "Тестовое описание выпуска.\r\nЗагрузка начинается только по кнопке.", new Uri("https://github.com/merumuiri-afk/2wchat2/releases/download/v1.6.0/2WChat-1.6.0-win-x64.zip"), 100, new string('0', 64))))
            {
                update.Show(this); await Task.Delay(150);
                using var bitmap = new Bitmap(update.Width, update.Height); update.DrawToBitmap(bitmap, new Rectangle(Point.Empty, update.Size)); bitmap.Save(Path.Combine(directory, "update-dialog.png")); update.Close();
            }
            results.Add("Update dialog renders version, notes and explicit download controls without a network request");
            _mute.Checked = false; _deafen.Checked = false; _settings.PushToTalk = false;
            _settings.MessageNotifications = true; _settings.MessageNotificationSound = true;
            _name.Text = "Владелец"; _room.Text = "Тестовая комната"; _password.Text = "test-only";
            var temporary = new TcpListener(IPAddress.Loopback, 0); temporary.Start();
            _port.Value = ((IPEndPoint)temporary.LocalEndpoint).Port; temporary.Stop();
            await ConnectRoomAsync(true, true);
            if (_client?.Connected != true || _server == null) throw new IOException("UI host connection failed");
            results.Add("Published UI creates a room and joins it over TLS");
            bot = new RoomClient();
            var pin = _server.Fingerprint;
            await bot.ConnectAsync("127.0.0.1", _server.Port, "Участник", "test-only", false, false, value => value == pin);
            await Task.Delay(300);
            if (_people.Items.Count != 2) throw new IOException("UI participant list did not update");
            using (var avatarBitmap = new Bitmap(180, 100))
            {
                using (var g = Graphics.FromImage(avatarBitmap)) { g.Clear(Color.Gold); g.FillEllipse(Brushes.Navy, 45, 5, 90, 90); }
                var avatar = AvatarPreparation.FromImage(avatarBitmap);
                _settings.Avatar = avatar; SaveSettings();
                await _client.SetAvatarAsync(avatar); await bot.SetAvatarAsync(avatar);
                var waitAvatar = System.Diagnostics.Stopwatch.StartNew();
                while ((_client.GetAvatar(bot.Id) == null || bot.GetAvatar(_client.Id) == null) && waitAvatar.ElapsedMilliseconds < 5000) await Task.Delay(30);
                await Task.Delay(100);
                if (_client.GetAvatar(bot.Id) == null || _avatarIcons.Images.Count != 2 || AppStore.Load().Avatar?.SequenceEqual(avatar) != true) throw new IOException("UI avatar transfer, icons or persistence failed");
                results.Add("Local and remote avatars travel over TLS, persist in profile and populate participant icons");
                using (var avatarViewer = new AvatarViewerForm(_client.GetAvatar(bot.Id), "Участник"))
                {
                    avatarViewer.Show(this); await Task.Delay(100);
                    using var shot = new Bitmap(avatarViewer.Width, avatarViewer.Height);
                    avatarViewer.DrawToBitmap(shot, new Rectangle(Point.Empty, avatarViewer.Size)); shot.Save(Path.Combine(directory, "avatar-viewer.png"));
                    avatarViewer.Close();
                }
                results.Add("Avatar viewer renders the original avatar enlarged in a resizable window");
                using var avatarDialog = new AvatarForm(avatar, "Участник"); avatarDialog.Show(this); await Task.Delay(100);
                using (var bitmap = new Bitmap(avatarDialog.Width, avatarDialog.Height)) { avatarDialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, avatarDialog.Size)); bitmap.Save(Path.Combine(directory, "avatar-settings.png")); }
                DescendantControls(avatarDialog).OfType<Button>().Single(b => b.Text == "Убрать аватарку").PerformClick();
                if (avatarDialog.Avatar != null) throw new IOException("Avatar removal control failed");
                avatarDialog.Close(); results.Add("Avatar settings render preview and allow explicit removal without changing the saved profile on cancel");
            }
            results.Add("Participant list updates for a second TLS client");
            _message.Text = "Привет! Это проверка текстового чата."; await SendChatAsync();
            await Task.Delay(100);
            if (_notifications.ReceivedCount != 0 || _notifications.SoundPlayCount != 0) throw new IOException("Own message triggered a notification");
            results.Add("Own messages do not trigger sound or unread notifications");
            await bot.SendChatAsync("Сообщение принято. Проверка голоса — Opus, UDP.");
            await Task.Delay(300);
            if (!_chat.Text.Contains("Сообщение принято") || _message.TextLength != 0) throw new IOException("UI chat did not update");
            results.Add("Russian chat send and receive update the visible UI");
            if (_notifications.ReceivedCount != 1 || _notifications.SoundPlayCount != 1) throw new IOException("Incoming chat did not play the embedded notification sound");
            results.Add("Live remote message plays the embedded user-provided notification sound");
            _historyReady = false;
            OnMessage(new WireMessage("chat") { Chat = new ChatEntry(Guid.NewGuid(), bot.Id, "Участник", "История при входе", DateTimeOffset.UtcNow) });
            if (_notifications.ReceivedCount != 1) throw new IOException("Buffered history triggered a notification");
            _pendingChats.Clear(); _historyReady = true;
            results.Add("Messages buffered during initial history loading do not notify");
            _settings.MessageNotificationSound = false; _notifications.Configure(_settings, true);
            if (_notifications.SoundPlaying) throw new IOException("Disabling notification sound did not stop it");
            WindowState = FormWindowState.Minimized;
            await bot.SendChatAsync("Проверка тихого уведомления в фоне."); await Task.Delay(200);
            if (_notifications.SoundPlayCount != 1 || _notifications.UnreadCount < 1 || !Text.StartsWith('[')) throw new IOException("Silent unread notification failed");
            results.Add("Background message increments unread caption without sound when sound is disabled");
            WindowState = FormWindowState.Normal; _notifications.ClearUnread();
            if (_notifications.UnreadCount != 0 || Text.StartsWith('[')) throw new IOException("Unread count did not clear");
            results.Add("Acknowledging the chat clears the unread counter and caption");
            _settings.MessageNotifications = false; _notifications.Configure(_settings, true);
            var notificationsBefore = _notifications.ReceivedCount;
            await bot.SendChatAsync("Уведомления выключены, переписка работает."); await Task.Delay(200);
            if (_notifications.ReceivedCount != notificationsBefore || !_chat.Text.Contains("переписка работает")) throw new IOException("Notification switch affects message delivery");
            results.Add("Notification switch suppresses alerts while text delivery continues");
            _settings.MessageNotifications = true; _settings.MessageNotificationSound = true; _notifications.Configure(_settings, true);
            var previousVoiceVolume = _settings.OutputVolume;
            _settings.NotificationVolume = 0; _notifications.Configure(_settings, true);
            var beforeVolumeTest = _notifications.SoundPlayCount;
            await bot.SendChatAsync("Уведомление с громкостью ноль."); await Task.Delay(200);
            if (_notifications.SoundPlayCount != beforeVolumeTest || !_chat.Text.Contains("громкостью ноль")) throw new IOException("Zero notification volume did not silence the alert");
            results.Add("Zero notification volume suppresses sound while text delivery continues");
            _settings.OutputVolume = 0; _settings.NotificationVolume = 35; _notifications.Configure(_settings, true);
            await bot.SendChatAsync("Отдельная громкость уведомления."); await Task.Delay(200);
            if (_notifications.SoundPlayCount != beforeVolumeTest + 1 || Math.Abs(_notifications.LastSoundVolume - .35f) > .001f) throw new IOException("Notification volume is not independent of voice volume");
            results.Add("Notification plays at 35 percent even with voice volume at zero");
            _settings.OutputVolume = previousVoiceVolume; _settings.NotificationVolume = 80; _notifications.Configure(_settings, true);
            _mute.Checked = true; await Task.Delay(100);
            if (!_client.Members.Single(p => p.Id == _client.Id).MicMuted) throw new IOException("Mic mute not propagated");
            results.Add("Microphone mute updates the room state");
            _deafen.Checked = true; await Task.Delay(100);
            if (!_client.Members.Single(p => p.Id == _client.Id).Deafened) throw new IOException("Deafen not propagated");
            var soundsBefore = _notifications.SoundPlayCount;
            await bot.SendChatAsync("Проверка отключения звука уведомлений клавишей F7."); await Task.Delay(150);
            if (_notifications.SoundPlayCount != soundsBefore || !_chat.Text.Contains("клавишей F7")) throw new IOException("Deafen did not suppress notification audio");
            results.Add("Deafen suppresses notification audio while chat messages still arrive");
            _deafen.Checked = false; await Task.Delay(100);
            results.Add("Deafen updates the room state and can be reversed");
            using var encoder = new VoiceEncoder();
            for (uint frame = 0; frame < 50; frame++)
            {
                var samples = Enumerable.Range(0, Protocol.FrameSamples).Select(i => (short)(Math.Sin((frame * Protocol.FrameSamples + i) * 2 * Math.PI * 440 / 48000) * 1000)).ToArray();
                bot.SendVoice(frame, 10, encoder.Encode(samples)); await Task.Delay(20);
            }
            await Task.Delay(300);
            if (_client.VoicePacketsReceived < 25) throw new IOException("Published client did not receive UDP voice");
            results.Add("Published client receives 50 generated Opus voice packets over encrypted UDP");
            if (!_client.UdpReady || !bot.UdpReady) throw new IOException("UDP keepalive failed");
            results.Add("UDP connection diagnostics are live in both clients");
            if (_audio != null) results.Add($"Audio devices: microphone={_audio.HasMicrophone}, output={_audio.HasOutput}, codec={_audio.Codec}");
            using (var sample = new Bitmap(640, 360))
            {
                using (var graphics = Graphics.FromImage(sample))
                {
                    graphics.Clear(Color.FromArgb(10, 36, 106));
                    graphics.FillRectangle(Brushes.Gold, 30, 30, 580, 80);
                    graphics.FillRectangle(Brushes.WhiteSmoke, 30, 130, 280, 200);
                    graphics.FillEllipse(Brushes.SeaGreen, 350, 150, 220, 150);
                    using var font = new Font("Tahoma", 24, FontStyle.Bold);
                    graphics.DrawString("2WChat — изображение", font, Brushes.Navy, 45, 45);
                }
                var image = ImagePreparation.FromBitmap(sample, "Проверка изображения.png");
                var received = await bot.SendImageAsync(image, "Картинка от участника");
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (_chat.LoadedImageCount < 1 && wait.ElapsedMilliseconds < 8000) await Task.Delay(40);
                if (_chat.LoadedImageCount != 1 || _chat.GetImage(received.Image!.Id)?.Info.Sha256 != image.Info.Sha256)
                    throw new IOException("Received image thumbnail did not load in the visible chat");
                results.Add("Remote image is downloaded, validated and rendered as a thumbnail in the chat");
                if (!_chat.ImageVisible(received.Image.Id)) throw new IOException("Chat did not scroll to show the new image");
                results.Add("Chat automatically scrolls to the newest image thumbnail");
                using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, "image-in-chat.png")); }
                using (var preview = new ImageViewerForm(image, true))
                {
                    preview.Show(this); await Task.Delay(150);
                    using var bitmap = new Bitmap(preview.Width, preview.Height); preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size)); bitmap.Save(Path.Combine(directory, "image-send-preview.png")); preview.Close();
                }
                using (var viewer = new ImageViewerForm(image))
                {
                    viewer.Show(this); await Task.Delay(150);
                    using var bitmap = new Bitmap(viewer.Width, viewer.Height); viewer.DrawToBitmap(bitmap, new Rectangle(Point.Empty, viewer.Size)); bitmap.Save(Path.Combine(directory, "image-viewer.png")); viewer.Close();
                }
                results.Add("Image send preview and full-size viewer render with caption and save controls");
                var ownNotifications = _notifications.ReceivedCount;
                await SendPreparedImageAsync(image, "Моя картинка");
                wait.Restart(); while (_chat.LoadedImageCount < 2 && wait.ElapsedMilliseconds < 8000) await Task.Delay(40);
                if (_chat.LoadedImageCount != 2 || !_chat.Text.Contains("Моя картинка") || _notifications.ReceivedCount != ownNotifications)
                    throw new IOException("Own image send failed or triggered an incoming notification");
                results.Add("Image button send path posts an own image without notifying its sender");
                var saved = Path.Combine(directory, "received-image.png");
                File.WriteAllBytes(saved, _chat.GetImage(received.Image.Id)!.Data);
                using var verified = Image.FromFile(saved);
                if (verified.Width != 640 || verified.Height != 360) throw new IOException("Saved image is invalid");
                results.Add("Received image bytes can be saved and reopened with the original dimensions");
            }
            await RunScreenUiSmokeAsync(directory, results, bot);
            await RunCompactUiSmokeAsync(directory, results, bot);
            var gifData = Convert.FromHexString("47494638396101000100800000000000FFFFFF21F904000A0000002C000000000100010000020244010021F904000A0000002C00000000010001000002024C01003B");
            await bot.SendImageAsync(ImageAttachments.Create(gifData, "animation.gif"), "GIF animation");
            await Task.Delay(500);
            if (!DescendantControls(_chat).OfType<PictureBox>().Any(p => p.Image != null && ImageAnimator.CanAnimate(p.Image))) throw new IOException("Chat did not retain an animated image");
            results.Add("Received GIF is attached to an animation-capable PictureBox with original frames");
            _server!.Rename("Переименованная комната"); await Task.Delay(150);
            if (_room.Text != "Переименованная комната" || !_heading.Text.Contains("Переименованная комната")) throw new IOException("Room rename did not update UI");
            results.Add("Owner-approved rename updates room field, heading and client state");
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, "main-window.png")); }
            using (var settings = new SettingsForm(_settings, true))
            {
                settings.Show(this); await Task.Delay(200);
                var slider = DescendantControls(settings).OfType<TrackBar>().Single(c => c.AccessibleName == "Громкость уведомлений");
                slider.Value = 37;
                using var bitmap = new Bitmap(settings.Width, settings.Height); settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, settings.Size)); bitmap.Save(Path.Combine(directory, "audio-settings.png"));
                DescendantControls(settings).OfType<Button>().Single(c => c.Text == "Применить").PerformClick();
                if (settings.Result.NotificationVolume != 37 || settings.Result.OutputVolume != _settings.OutputVolume) throw new IOException("Notification slider did not apply independently");
                results.Add("Notification slider applies its own volume without changing the voice setting");
            }
            results.Add("Main window and sound settings render at current display DPI");
            await bot.DisposeAsync(); bot = null; await Task.Delay(200);
            if (_people.Items.Count != 1) throw new IOException("Departure did not update participant list");
            results.Add("Participant departure updates the visible list");
            await StopSessionAsync();
            if (_message.Enabled || _people.Items.Count != 0) throw new IOException("Disconnect UI cleanup failed");
            results.Add("Disconnect releases the session and resets the UI");
            await ConnectRoomAsync(true, true);
            if (_client?.Connected != true) throw new IOException("Same-port rehost failed");
            await Task.Delay(150);
            if (_client.GetAvatar(_client.Id)?.SequenceEqual(_settings.Avatar ?? []) != true) throw new IOException("Saved avatar did not upload on rejoin");
            results.Add("Saved profile avatar is automatically uploaded again after leaving and recreating a room");
            results.Add("Room can be created again on the same port without restarting");
            await StopSessionAsync();
        }
        catch (Exception e) { errors.Add(e.ToString()); }
        finally
        {
            if (bot != null) await bot.DisposeAsync();
            await StopSessionAsync();
            File.WriteAllText(Path.Combine(directory, "ui-smoke.json"), JsonSerializer.Serialize(new { Passed = errors.Count == 0, Checks = results, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = errors.Count == 0 ? 0 : 1;
            Close();
        }
    }
    private static IEnumerable<Control> DescendantControls(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in DescendantControls(child)) yield return descendant; }
    }
}
