param([string]$SmokeTestRoot = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'src\TwoWChat\TwoWChat.csproj'))
$version = $project.Project.PropertyGroup.Version
$compiler = Join-Path $root '.tools\inno\ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $download = Join-Path $root '.tools\innosetup-7.1.1-x64.exe'
    $expected = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
    if (!(Test-Path -LiteralPath $download)) { Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $download }
    if ((Get-FileHash -LiteralPath $download).Hash.ToLowerInvariant() -ne $expected) { throw 'Inno Setup SHA-256 mismatch' }
    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Inno Setup signature mismatch' }
    $destination = Split-Path -Parent $compiler
    $process = Start-Process -FilePath $download -ArgumentList @('/PORTABLE=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $destination + '"')) -PassThru -WindowStyle Hidden
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw 'Portable compiler setup failed' }
}
$app = Join-Path $root 'dist\2WChat\2WChat.exe'
if (!(Test-Path -LiteralPath $app)) { throw 'Build the portable app first: Tools/Build.ps1' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne ($version + '.0')) { throw 'Published app version differs from project version' }
$arguments = @('/Qp', ('/DAppVersion=' + $version))
if ($SmokeTestRoot) { $arguments += ('/DSmokeTestRoot=' + [IO.Path]::GetFullPath($SmokeTestRoot)) }
$arguments += (Join-Path $root 'packaging\2WChat.iss')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
if (!$SmokeTestRoot) {
    $installer = Join-Path $root ('dist\2WChat-' + $version + '-Setup.exe')
    $hash = (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $root 'dist\Setup-SHA256SUMS.txt') -Value ($hash + '  ' + (Split-Path -Leaf $installer)) -Encoding ascii
    Write-Output ('Ready: ' + $installer)
}
