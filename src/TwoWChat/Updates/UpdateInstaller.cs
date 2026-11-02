using System.Diagnostics;
using System.Text.Json;

namespace TwoWChat.Updates;

public sealed record UpdatePlan(string Target, string Sha256, string Version, int ParentPid, long ParentStartTicks, string DataDirectory);

public static class UpdateInstaller
{
    public static void Launch(string folder, UpdateRelease release)
    {
        var target = Environment.ProcessPath!;
        if (!Path.GetFileName(target).Equals("2WChat.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Обновление доступно в автономной сборке 2WChat.exe.");
        // Check write access before shutdown (Program Files requires moving the portable app to a writable folder).
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".2wchat-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { } File.Delete(probe);
        var helper = Path.Combine(folder, "2WChat.UpdateHelper.exe");
        File.Copy(target, helper, false);
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan(target, release.Sha256, release.Version.ToString(), parent.Id, parent.StartTime.ToUniversalTime().Ticks, AppStore.Folder);
        var planPath = Path.Combine(folder, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = folder };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        if (Process.Start(start) is not { } helperProcess) throw new IOException("Не удалось запустить установщик.");
        helperProcess.Dispose();
    }

    public static async Task ApplyAsync(string planPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        if (!string.Equals(Path.GetDirectoryName(Environment.ProcessPath), folder, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(Environment.ProcessPath) != "2WChat.UpdateHelper.exe" || Path.GetFileName(planPath) != "plan.json")
            throw new InvalidDataException("Некорректный запуск установщика.");
        if (new FileInfo(planPath).Length > 16384) throw new InvalidDataException("Некорректный план обновления.");
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath)) ?? throw new InvalidDataException("Пустой план обновления.");
        var target = Path.GetFullPath(plan.Target);
        var targetDirectory = Path.GetDirectoryName(target)!;
        if (!Path.IsPathFullyQualified(plan.Target) || !Path.GetFileName(target).Equals("2WChat.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetDirectory, folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(target) ||
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Некорректный путь программы.");
        var version = UpdateService.ParseVersion(plan.Version) ?? throw new InvalidDataException("Некорректная версия.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var package = Path.Combine(folder, "package.zip");
        await UpdateService.VerifyHashAsync(package, plan.Sha256, timeout.Token);
        var candidate = Path.Combine(folder, "install.exe");
        UpdateService.ExtractExecutable(package, candidate, version);
        try
        {
            using var parent = Process.GetProcessById(plan.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(wait.Token);
            }
        }
        catch (ArgumentException) { } // Parent has already exited.
        var sibling = Path.Combine(targetDirectory, ".2wchat-update-" + Guid.NewGuid().ToString("N") + ".exe");
        var backup = target + ".previous";
        try
        {
            File.Copy(candidate, sibling, false);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(sibling, target, backup, true); break; }
                catch (IOException) when (attempt < 20) { await Task.Delay(500); }
            }
            try { Restart(target, plan.DataDirectory); }
            catch
            {
                // Restore the old executable if Windows refuses to launch the new one.
                File.Copy(backup, target, true);
                Restart(target, plan.DataDirectory);
                throw;
            }
            File.WriteAllText(Path.Combine(folder, "result.txt"), "Installed " + version);
        }
        finally { if (File.Exists(sibling)) File.Delete(sibling); }
    }

    private static void Restart(string target, string dataDirectory)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetFullPath(dataDirectory));
        if (Process.Start(start) is not { } process) throw new IOException("Не удалось перезапустить 2WChat.");
        process.Dispose();
    }
}
