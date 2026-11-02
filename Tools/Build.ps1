param([switch]$SkipChecks, [switch]$AudioChecks, [switch]$SkipUiSmoke)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'src\TwoWChat\TwoWChat.csproj'))
$version = $project.Project.PropertyGroup.Version
Set-Location -LiteralPath $root
$dotnet = & (Join-Path $PSScriptRoot 'Get-Sdk.ps1')
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& $dotnet build 'tests\TwoWChat.Checks\TwoWChat.Checks.csproj' -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (!$SkipChecks) {
    $checkArgs = @('tests\TwoWChat.Checks\bin\Release\net10.0-windows\TwoWChat.Checks.dll', 'artifacts\checks')
    if ($AudioChecks) { $checkArgs += '--audio' }
    & $dotnet @checkArgs
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
}
$output = Join-Path $root 'dist\2WChat'
& $dotnet publish 'src\TwoWChat\TwoWChat.csproj' -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=embedded' '-p:DebugSymbols=false' -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $root 'README-RU.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\Allow-Firewall.ps1') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\Allow-Firewall.cmd') -Destination $output -Force
$licenseOutput = Join-Path $output 'licenses'
New-Item -ItemType Directory -Force -Path $licenseOutput | Out-Null
$runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $root 'src\TwoWChat\bin\Release\net10.0-windows\win-x64\2WChat.runtimeconfig.json') | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $package = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $packagePath = Join-Path $env:NUGET_PACKAGES ($package + '\' + $framework.version)
    Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY)' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseOutput ($framework.name + '-' + $_.Name)) -Force
    }
}
if (!$SkipUiSmoke) {
$report = Join-Path $root 'artifacts\ui-published'
$process = Start-Process -FilePath (Join-Path $output '2WChat.exe') -ArgumentList @('--ui-smoke', ('"' + $report + '"')) -PassThru -WindowStyle Hidden
if (!$process.WaitForExit(30000)) { throw 'Published UI smoke timeout; inspect the running app and artifacts.' }
$process.Refresh()
if ($process.ExitCode -ne 0) { throw 'Published UI smoke failed; see artifacts\ui-published\ui-smoke.json' }
$smoke = Get-Content -Raw -LiteralPath (Join-Path $report 'ui-smoke.json') | ConvertFrom-Json
if (!$smoke.Passed) { throw 'Published UI smoke report failed' }
}
$zip = Join-Path $root ('dist\2WChat-' + $version + '-win-x64.zip')
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $root 'dist\SHA256SUMS.txt') -Value ($hash + '  ' + (Split-Path -Leaf $zip)) -Encoding ascii
Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
Write-Host ('Ready: ' + (Join-Path $output '2WChat.exe'))
