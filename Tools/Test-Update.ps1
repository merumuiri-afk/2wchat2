param([string]$OldPackage = '')
$ErrorActionPreference = 'Stop'
if (!('TwoWChatInstallerProbe' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class TwoWChatInstallerProbe {
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr[] Windows(int processId) {
        var windows = new List<IntPtr>();
        EnumWindows((window, ignored) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == processId) { var text = new StringBuilder(256); GetWindowText(window, text, 256); if (text.ToString().StartsWith("2WChat")) windows.Add(window); }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    public static void Close(int processId) { foreach(var window in Windows(processId)) PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); }
}
'@
}
$root = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'src\TwoWChat\TwoWChat.csproj'))
$version = $project.Project.PropertyGroup.Version
$fixture = Join-Path $root ('artifacts\installer-' + [Guid]::NewGuid().ToString('N'))
$targetFolder = Join-Path $fixture 'program with spaces'
$stage = Join-Path $fixture 'stage'
$profile = Join-Path $fixture 'profile'
New-Item -ItemType Directory -Force -Path $targetFolder,$stage,$profile | Out-Null
$target = Join-Path $targetFolder '2WChat.exe'
if ($OldPackage) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $OldPackage).Path)
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('2WChat.exe'), $target) } finally { $zip.Dispose() }
} else { Copy-Item -LiteralPath (Join-Path $root 'dist\2WChat\2WChat.exe') -Destination $target }
$oldHash = (Get-FileHash -LiteralPath $target).Hash
$profileSettings = @{ Name = 'Installer fixture'; NotificationVolume = 37; AutoCheckUpdates = $false; TrustedHosts = @{ '127.0.0.1:48500' = 'fixture-pin' } }
$profileSettings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $profile 'room-identity.pfx'), 'unchanged fixture identity')
[IO.File]::WriteAllText((Join-Path $targetFolder 'custom-file.txt'), 'keep local file')
$helperPath = Join-Path $stage '2WChat.UpdateHelper.exe'
Copy-Item -LiteralPath (Join-Path $root 'dist\2WChat\2WChat.exe') -Destination $helperPath
$package = Join-Path $stage 'package.zip'
Copy-Item -LiteralPath (Join-Path $root ('dist\2WChat-' + $version + '-win-x64.zip')) -Destination $package
$application = $null; $helper = $null; $restarted = $null
try {
    $application = Start-Process -FilePath $target -ArgumentList @('--data-dir', ('"' + $profile + '"')) -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 100; $application.Refresh() } until ([TwoWChatInstallerProbe]::Windows($application.Id).Length -gt 0 -or $application.HasExited -or [DateTime]::UtcNow -gt $deadline)
    if ($application.HasExited -or [TwoWChatInstallerProbe]::Windows($application.Id).Length -eq 0) { throw 'Fixture app did not open' }
    $null = $application.WaitForInputIdle(10000)
    $plan = @{ Target = $target; Sha256 = (Get-FileHash -LiteralPath $package).Hash; Version = $version; ParentPid = $application.Id; ParentStartTicks = $application.StartTime.ToUniversalTime().Ticks; DataDirectory = $profile }
    $planPath = Join-Path $stage 'plan.json'
    $plan | ConvertTo-Json | Set-Content -LiteralPath $planPath -Encoding utf8
    $helper = Start-Process -FilePath $helperPath -ArgumentList @('--apply-update', ('"' + $planPath + '"')) -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path -LiteralPath (Join-Path $stage 'install.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Start-Sleep -Milliseconds 500
    if ($application.HasExited -or (Get-FileHash -LiteralPath $target).Hash -ne $oldHash -or (Test-Path -LiteralPath ($target + '.previous'))) { throw 'Installer did not wait for the running parent' }
    [TwoWChatInstallerProbe]::Close($application.Id)
    if (!$application.WaitForExit(10000)) { throw 'Fixture app did not close' }
    if (!$helper.WaitForExit(30000)) { throw 'Installer timed out' }
    $helper.Refresh()
    if ($helper.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $stage 'result.txt'))) { throw 'Installer failed' }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $restarted = Get-Process -Name '2WChat' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target -and $_.Id -ne $application.Id } | Select-Object -First 1
        if ($restarted) { $restarted.Refresh() }
        if (!$restarted -or [TwoWChatInstallerProbe]::Windows($restarted.Id).Length -eq 0) { Start-Sleep -Milliseconds 100 }
    } until (($restarted -and [TwoWChatInstallerProbe]::Windows($restarted.Id).Length -gt 0) -or [DateTime]::UtcNow -gt $deadline)
    if (!$restarted -or [TwoWChatInstallerProbe]::Windows($restarted.Id).Length -eq 0) { throw 'Updated application did not restart' }
    $expectedHash = (Get-FileHash -LiteralPath (Join-Path $root 'dist\2WChat\2WChat.exe')).Hash
    if ((Get-FileHash -LiteralPath $target).Hash -ne $expectedHash -or (Get-FileHash -LiteralPath ($target + '.previous')).Hash -ne $oldHash) { throw 'Executable or backup differs from the expected bytes' }
    $settings = Get-Content -Raw -LiteralPath (Join-Path $profile 'settings.json') | ConvertFrom-Json
    if ($settings.NotificationVolume -ne 37 -or $settings.Name -ne 'Installer fixture' -or $settings.TrustedHosts.'127.0.0.1:48500' -ne 'fixture-pin') { throw 'Profile was not preserved' }
    if ([IO.File]::ReadAllText((Join-Path $profile 'room-identity.pfx')) -ne 'unchanged fixture identity' -or [IO.File]::ReadAllText((Join-Path $targetFolder 'custom-file.txt')) -ne 'keep local file') { throw 'Unrelated user files were changed' }
    @{ Passed = $true; OldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($target + '.previous').FileVersion; NewVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($target).FileVersion; ParentWait = $true; Restart = $true; Backup = $true; Profile = $true; Identity = $true; CustomFile = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'installer-smoke.json') -Encoding utf8
    Write-Output ('PASS installer replacement, parent wait, restart, rollback backup and profile preservation: ' + $fixture)
} finally {
    foreach ($process in @($application, $restarted)) {
        if ($process) { $process.Refresh(); if (!$process.HasExited) { [TwoWChatInstallerProbe]::Close($process.Id); if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id } }; $process.Dispose() }
    }
    if ($helper) { $helper.Refresh(); if (!$helper.HasExited) { Stop-Process -Id $helper.Id }; $helper.Dispose() }
}
