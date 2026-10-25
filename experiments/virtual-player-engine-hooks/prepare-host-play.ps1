param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$package = Join-Path $repo '.local/virtual-player-network-build/Mods/VirtualPlayerEngineProbe.tmod'
if (!(Test-Path -LiteralPath $package)) { throw 'Run the G2 build and verify scripts first.' }
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$profile = Join-Path $repo ('.local/virtual-player-network-host-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $profile) { throw 'Profile already exists; retry to choose a new timestamp.' }
New-Item -ItemType Directory -Path (Join-Path $profile 'Mods') | Out-Null
Copy-Item -LiteralPath $package -Destination (Join-Path $profile 'Mods/VirtualPlayerEngineProbe.tmod')
Set-Content -LiteralPath (Join-Path $profile 'Mods/enabled.json') -Value '["VirtualPlayerEngineProbe"]'
$launcher = @'
param([string]$TModLoaderPath = '__TML__')
$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'Mods/VirtualPlayerEngineProbe.tmod'
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '__HASH__') { throw 'G2 package changed; build and verify before preparing a new profile.' }
$enabled = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Mods/enabled.json') -Raw | ConvertFrom-Json
$extra = @($enabled | Where-Object { $_ -notin @('VirtualPlayerEngineProbe', 'ModLoader') })
if ($extra.Count) { throw ('G2 isolation: disable these mods before launch: ' + ($extra -join ', ')) }
Push-Location $TModLoaderPath
try { & (Join-Path $TModLoaderPath 'start-tModLoader.bat') -tmlsavedirectory $PSScriptRoot -modpath (Join-Path $PSScriptRoot 'Mods') }
finally { Pop-Location }
'@
Set-Content -LiteralPath (Join-Path $profile 'start-host-play.ps1') -Value $launcher.Replace('__HASH__', $packageHash).Replace('__TML__', $TModLoaderPath.Replace("'", "''"))
Set-Content -LiteralPath (Join-Path $profile 'BUILD.txt') -Value @('VirtualPlayerEngineProbe 0.2.1 (G2)', ('SHA256=' + $packageHash),
    'Offline validation only. SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.',
    'Run start-host-play.ps1 manually; create a fresh Classic character/world; Multiplayer > Host & Play.',
    '/vph status, /vph spawn, /vpc <slot>, /vph debug-style, /vph resync, /vph despawn.')
Write-Output ('Prepared: ' + $profile)
Write-Output ('SHA256: ' + $packageHash)
Write-Output ('Manual launch: ' + (Join-Path $profile 'start-host-play.ps1'))
