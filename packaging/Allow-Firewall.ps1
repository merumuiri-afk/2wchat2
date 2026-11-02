param(
    [ValidateRange(1024,65535)][int]$Port = 48500,
    [string[]]$RemoteAddress = @('LocalSubnet', '26.0.0.0/8', '25.0.0.0/8', '100.64.0.0/10')
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '2WChat.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Keep this script in the same folder as 2WChat.exe.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Forward only validated, unambiguous values to the elevated helper.
    foreach ($address in $RemoteAddress) { if ($address -notmatch '^[a-zA-Z0-9.:/]+$') { throw 'Invalid remote address' } }
    $list = ($RemoteAddress | ForEach-Object { "'$_'" }) -join ','
    $quotedPath = $PSCommandPath.Replace("'", "''")
    $command = "& '$quotedPath' -Port $Port -RemoteAddress @($list)"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Wait
    return
}
foreach ($protocol in @('TCP','UDP')) {
    $rule = "2WChat-$protocol-$Port"
    $existing = Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue
    if ($existing) { $existing | Remove-NetFirewallRule }
    New-NetFirewallRule -Name $rule -DisplayName "2WChat $protocol $Port" -Direction Inbound -Action Allow -Program $exe -Protocol $protocol -LocalPort $Port -Profile Any -RemoteAddress $RemoteAddress | Out-Null
}
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.MessageBox]::Show("2WChat: TCP + UDP $Port allowed for this program on local/VPN subnets.", '2WChat') | Out-Null
