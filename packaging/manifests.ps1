# Package manager manifests for a published release: .\packaging\manifests.ps1 1.2.0
# Writes into packaging\out\<version>\:
#   winget\manifests\a\Alukkart\ClipKeeper\<version>\*.yaml — the first submission to microsoft/winget-pkgs (a pull request
#     with this folder); later versions go there from release.yml ("WinGet"), which updates the previous manifest;
#   scoop\clipkeeper.json — for a Scoop bucket; checkver/autoupdate let the bucket follow new releases by itself.
# The hashes of the setup and the zip come from the release's SHA256SUMS.txt, the release notes from CHANGELOG.md.
# Check: winget validate --manifest <the winget folder>; scoop install .\packaging\out\<version>\scoop\clipkeeper.json
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v')
$repo = 'Alukkart/ClipKeeper'
$root = Split-Path $PSScriptRoot -Parent
$zip = "ClipKeeper-$Version.zip"
$zipUrl = "https://github.com/$repo/releases/download/v$Version/$zip"
$out = Join-Path $PSScriptRoot "out\$Version"

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$sums = (Invoke-WebRequest "https://github.com/$repo/releases/download/v$Version/SHA256SUMS.txt" -UseBasicParsing).Content
if ($sums -is [byte[]]) { $sums = [Text.Encoding]::ASCII.GetString($sums) }
function HashOf($name) {
    $m = [regex]::Match($sums, "(?m)^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($name))\s*$")
    if ($m.Success) { $m.Groups[1].Value } else { $null }
}
$sha = HashOf $zip
if (-not $sha) { throw "SHA256SUMS.txt of v$Version has no $zip" }
$setup = "ClipKeeper-$Version-setup.exe"   # from 1.2.0 on
$setupSha = HashOf $setup

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/v$Version" -Headers @{ 'User-Agent' = 'ClipKeeper-manifests' }
$date = ([datetime]$release.published_at).ToUniversalTime().ToString('yyyy-MM-dd')

# this version's CHANGELOG section, as the release notes show it
$log = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
$s = [regex]::Match($log, "(?ms)^## \[$([regex]::Escape($Version))\][^\r\n]*\r?\n(.*?)(?=^## \[|^\[[^\]]+\]:|\z)")
if (-not $s.Success) { throw "CHANGELOG.md has no section for $Version" }
$notes = ($s.Groups[1].Value.Trim() -replace '\r?\n[ \t]+(?=\S)', ' ') -split '\r?\n' | ForEach-Object { "  $_" }
$notes = $notes -join "`n"

$utf8 = New-Object Text.UTF8Encoding($false)
function Save($path, $text) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, ($text -replace '\r\n', "`n"), $utf8)
    "  $path"
}

$schema = '1.12.0'
$id = 'Alukkart.ClipKeeper'
$w = Join-Path $out "winget\manifests\a\Alukkart\ClipKeeper\$Version"
"winget:"

Save (Join-Path $w "$id.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

# the setup first — what winget installs unless asked for --installer-type portable: Inno Setup, for one user, a Start menu
# shortcut; the same AppId as installer\ClipKeeper.iss, so winget finds it in "Apps" (and ClipKeeper's own updates keep
# its version there up to date).
# Then the portable zip: winget unpacks it into its Packages folder and puts that folder on PATH (ClipKeeper needs its
# ffmpeg\ next to it, so no symlink). ClipKeeper sees that folder and keeps its data in %LOCALAPPDATA%\ClipKeeper
$setupEntry = if ($setupSha) { @"
- Architecture: x64
  InstallerType: inno
  Scope: user
  InstallerUrl: https://github.com/$repo/releases/download/v$Version/$setup
  InstallerSha256: $($setupSha.ToUpper())
  UpgradeBehavior: install
  ProductCode: '{F0418725-5FD7-4201-B1E0-62BAE55CB01B}_is1'

"@ } else { '' }
Save (Join-Path $w "$id.installer.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
Platform:
- Windows.Desktop
MinimumOSVersion: 10.0.17763.0
ReleaseDate: $date
Installers:
$setupEntry- Architecture: x64
  InstallerType: zip
  NestedInstallerType: portable
  NestedInstallerFiles:
  - RelativeFilePath: ClipKeeper\ClipKeeper.exe
  ArchiveBinariesDependOnPath: true
  InstallerUrl: $zipUrl
  InstallerSha256: $($sha.ToUpper())
ManifestType: installer
ManifestVersion: $schema
"@

$tags = @('clips', 'game-clips', 'obs', 'obs-studio', 'obs-websocket', 'recording', 'replay-buffer', 'streaming', 'video-trimmer', 'gaming')
$tagLines = ($tags | ForEach-Object { "- $_" }) -join "`n"

Save (Join-Path $w "$id.locale.en-US.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Alukkart
PublisherUrl: https://github.com/Alukkart
PublisherSupportUrl: https://github.com/$repo/issues
Author: Alukkart
PackageName: ClipKeeper
PackageUrl: https://alukkart.github.io/ClipKeeper/
License: MIT
LicenseUrl: https://github.com/$repo/blob/HEAD/LICENSE
ShortDescription: A tray companion for OBS that keeps your recording from breaking and keeps your clips.
Description: |-
  ClipKeeper watches OBS from the outside, through obs-websocket, and fixes what breaks by itself: lost devices after a driver or audio software update, a crashed replay buffer, a silent source, a black screen.
  It also keeps your clips: a library by game with covers, a frame-exact trim editor with cuts and per-track volume, sharing sized for Discord or Telegram, and statistics of what you record and keep.
  Unofficial, not affiliated with the OBS Project. Needs OBS Studio 28 or newer.
Moniker: clipkeeper
Tags:
$tagLines
ReleaseNotes: |-
$notes
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version
Documentations:
- DocumentLabel: README
  DocumentUrl: https://github.com/$repo#readme
ManifestType: defaultLocale
ManifestVersion: $schema
"@

Save (Join-Path $w "$id.locale.ru-RU.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: ru-RU
Publisher: Alukkart
PackageName: ClipKeeper
License: MIT
ShortDescription: Компаньон OBS в трее — не даёт записи сломаться и хранит твои клипы.
Description: |-
  ClipKeeper следит за OBS снаружи, через obs-websocket, и сам чинит то, что ломается: потерянные устройства после обновления драйвера или звуковых программ, упавший буфер повтора, замолчавший источник, чёрный экран.
  А ещё хранит клипы: библиотека по играм с обложками, покадровый редактор обрезки с вырезами и громкостью по дорожкам, отправка под размер Discord или Telegram и статистика того, что записываешь и оставляешь.
  Неофициальный, не связан с OBS Project. Нужен OBS Studio 28 или новее.
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version
Documentations:
- DocumentLabel: README
  DocumentUrl: https://github.com/$repo/blob/HEAD/README.ru.md
ManifestType: locale
ManifestVersion: $schema
"@

# Scoop: the zip's ClipKeeper\ folder becomes apps\clipkeeper\<version>\, a Start menu shortcut goes to "Scoop Apps".
# The shortcut and autostart point at apps\clipkeeper\current\, which follows updates
"scoop:"
Save (Join-Path $out 'scoop\clipkeeper.json') @"
{
    "version": "$Version",
    "description": "A tray companion for OBS: keeps your recording from breaking and keeps your clips (recording guard, clip library, trim editor).",
    "homepage": "https://alukkart.github.io/ClipKeeper/",
    "license": "MIT",
    "notes": [
        "ClipKeeper keeps its settings and data in %LOCALAPPDATA%\\ClipKeeper; they stay after an uninstall.",
        "Update it with: scoop update clipkeeper"
    ],
    "architecture": {
        "64bit": {
            "url": "$zipUrl",
            "hash": "$($sha.ToLower())"
        }
    },
    "extract_dir": "ClipKeeper",
    "shortcuts": [
        [
            "ClipKeeper.exe",
            "ClipKeeper"
        ]
    ],
    "checkver": {
        "github": "https://github.com/$repo"
    },
    "autoupdate": {
        "architecture": {
            "64bit": {
                "url": "https://github.com/$repo/releases/download/v`$version/ClipKeeper-`$version.zip"
            }
        },
        "hash": {
            "url": "`$baseurl/SHA256SUMS.txt"
        }
    }
}

"@
