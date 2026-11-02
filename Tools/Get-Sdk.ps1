param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root '.tools'
$dotnet = Join-Path $tools 'dotnet\dotnet.exe'
if (Test-Path -LiteralPath $dotnet) { Write-Output $dotnet; return }
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$archive = Join-Path $tools 'dotnet-sdk.zip'
$url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip'
$expected = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected) {
    Write-Host 'Downloading the official .NET 10 SDK (about 287 MB)...'
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected) { throw 'SDK SHA-512 mismatch. Retry the download.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $tools 'dotnet') -Force
Write-Output $dotnet
