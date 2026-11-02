using TwoWChat.Updates;

namespace TwoWChat.UI;

public sealed partial class MainForm
{
    private readonly CancellationTokenSource _updateStop = new();
    private bool _checkingUpdate;
    private UpdateRelease? _availableUpdate;
    private ToolStripMenuItem? _updateMenu;

    private void AddUpdateMenu(ToolStripMenuItem tools)
    {
        tools.DropDownItems.Add(new ToolStripSeparator());
        _updateMenu = new ToolStripMenuItem("Проверить обновления…");
        _updateMenu.Click += async (_, _) => await CheckUpdatesAsync(true);
        tools.DropDownItems.Add(_updateMenu);
        var automatic = new ToolStripMenuItem("Проверять обновления при запуске") { CheckOnClick = true, Checked = _settings.AutoCheckUpdates };
        automatic.CheckedChanged += (_, _) => { _settings.AutoCheckUpdates = automatic.Checked; SaveSettings(); };
        tools.DropDownItems.Add(automatic);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdate || _closing || _updateStop.IsCancellationRequested) return;
        if (!manual && (!_settings.AutoCheckUpdates || DateTimeOffset.UtcNow - _settings.LastUpdateCheckUtc < TimeSpan.FromHours(24))) return;
        if (manual && _availableUpdate != null) { OfferUpdate(_availableUpdate); return; }
        _checkingUpdate = true;
        if (_updateMenu != null) _updateMenu.Enabled = false;
        try
        {
            using var service = new UpdateService();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateStop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            _availableUpdate = await service.CheckAsync(timeout.Token);
            if (_updateStop.IsCancellationRequested) return;
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow; SaveSettings();
            if (_availableUpdate != null)
            {
                if (_updateMenu != null) _updateMenu.Text = "Установить " + _availableUpdate.Version.ToString(3) + "…";
                AppendNotice("Доступна версия " + _availableUpdate.Version.ToString(3) + ". Сервис → Установить обновление. Сначала выйдите из комнаты.");
                if (manual) OfferUpdate(_availableUpdate);
            }
            else if (manual) ShowError("Новых готовых выпусков нет. Текущая версия: " + UpdateService.CurrentVersion.ToString(3) + ".");
        }
        catch (OperationCanceledException) { if (manual && !_updateStop.IsCancellationRequested) ShowError("Сервер обновлений не ответил вовремя."); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            AppStore.Log("Update check: " + e.Message);
            if (manual && !_updateStop.IsCancellationRequested) ShowError("Не удалось проверить обновления: " + e.Message + "\nМожно повторить позднее; чат продолжает работать.");
        }
        finally { _checkingUpdate = false; if (_updateMenu != null && !IsDisposed) _updateMenu.Enabled = true; }
    }

    private void OfferUpdate(UpdateRelease release)
    {
        if (_busy || _stopping || _client != null || _server != null)
        {
            ShowError("Обновление готово. Сначала отключитесь от комнаты. Если вы владелец, выход закроет комнату для участников.");
            return;
        }
        using var form = new UpdateForm(release);
        if (form.ShowDialog(this) == DialogResult.OK) Close();
    }
}
