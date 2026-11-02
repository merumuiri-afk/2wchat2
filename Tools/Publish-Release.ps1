param([switch]$InstallerOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'src\TwoWChat\TwoWChat.csproj'))
$version = $project.Project.PropertyGroup.Version
$repository = 'merumuiri-afk/2wchat2'
$zip = Join-Path $root ('dist\2WChat-' + $version + '-win-x64.zip')
$sums = Join-Path $root 'dist\SHA256SUMS.txt'
if (!(Test-Path -LiteralPath $zip) -or !(Test-Path -LiteralPath $sums)) { throw 'Run Tools/Build.ps1 first' }
$expectedHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -Raw -LiteralPath $sums).Trim() -ne ($expectedHash + '  ' + (Split-Path -Leaf $zip))) { throw 'SHA256SUMS does not match the package' }
$accessToken = $env:GH_TOKEN
if (!$accessToken) {
    $env:GCM_INTERACTIVE = 'never'
    $env:GIT_TERMINAL_PROMPT = '0'
    $lines = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $passwordLine = $lines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (!$passwordLine) { throw 'Sign into GitHub through Git Credential Manager, or set GH_TOKEN in the process environment. Never put tokens in source files.' }
    $accessToken = $passwordLine.Substring(9)
}
$headers = @{ Authorization = ('Bearer ' + $accessToken); 'User-Agent' = '2WChat-release'; Accept = 'application/vnd.github+json' }
$api = 'https://api.github.com/repos/' + $repository
$releases = Invoke-RestMethod -Uri ($api + '/releases') -Headers $headers
$release = $releases | Where-Object { $_.tag_name -eq ('v' + $version) } | Select-Object -First 1
if ($release -and !$release.draft -and !$InstallerOnly) { throw 'This release is already public; publish a new version instead of replacing its assets' }
if ($InstallerOnly -and !$release) { throw 'Publish the app release before adding an installer' }
if (!$release) {
    $body = @{ tag_name = ('v' + $version); target_commitish = 'main'; name = ('2WChat ' + $version); body = (Get-Content -Raw -LiteralPath (Join-Path $root 'RELEASE-NOTES.md')); draft = $true; prerelease = $false } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri ($api + '/releases') -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
}
$setup = Join-Path $root ('dist\2WChat-' + $version + '-Setup.exe')
$setupSums = Join-Path $root 'dist\Setup-SHA256SUMS.txt'
$files = @($zip, $sums)
if ($InstallerOnly) { $files = @($setup, $setupSums) }
elseif (Test-Path -LiteralPath $setup) { $files += @($setup, $setupSums) }
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath $file)) { throw ('Missing release asset: ' + $file) }
    $name = Split-Path -Leaf $file
    $existing = @($release.assets) | Where-Object { $_.name -eq $name }
    if ($existing) { throw ('Asset already exists in the draft: ' + $name + '. Inspect it on GitHub before retrying.') }
    $upload = 'https://uploads.github.com/repos/' + $repository + '/releases/' + $release.id + '/assets?name=' + [Uri]::EscapeDataString($name)
    $asset = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType 'application/octet-stream' -InFile $file
    if ($name.EndsWith('.zip') -and $asset.digest -ne ('sha256:' + $expectedHash)) { throw 'GitHub asset digest differs from local package; draft remains unpublished' }
    if ($name.EndsWith('.exe') -and $asset.digest -ne ('sha256:' + (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())) { throw 'GitHub installer digest differs from local setup' }
}
if ($InstallerOnly) { Write-Output ('Installer added: ' + $release.html_url); return }
$published = Invoke-RestMethod -Method Patch -Uri ($api + '/releases/' + $release.id) -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}'
Write-Output ('Published: ' + $published.html_url)
