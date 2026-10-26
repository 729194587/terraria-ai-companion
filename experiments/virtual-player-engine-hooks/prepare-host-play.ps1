param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader', [switch]$Fresh)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
$repo=$paths.Repository
$package = $paths.Package
if (!(Test-Path -LiteralPath $package)) { throw 'Run build.ps1 and verify.ps1 first.' }
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$runningTml = @(Get-Process -Name dotnet,tModLoader -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -eq 'tModLoader' -or $_.Path -like '*tModLoader*' })
if ($runningTml.Count) { throw 'Close tModLoader and its Host & Play server before updating/copying the test profile.' }
$profile = $paths.Host
if ($Fresh) {
    $profile = $paths.Host+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')
    if (Test-Path -LiteralPath $profile) { throw 'Fresh profile already exists; retry to choose a new timestamp.' }
}
$deployedPackage = Join-Path $profile 'Mods/VirtualPlayerEngineProbe.tmod'
if (Test-Path -LiteralPath $profile) {
    if (!(Test-Path -LiteralPath $deployedPackage) -or (Get-FileHash -LiteralPath $deployedPackage -Algorithm SHA256).Hash -ne $packageHash) {
        $backup = Join-Path $paths.Backups (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
        if (Test-Path -LiteralPath $backup) { throw 'Backup already exists; retry instead of overwriting it.' }
        New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $profile -Destination $backup -Recurse
        Write-Output ('Backup before update: ' + $backup)
    }
} elseif (!$Fresh) {
    # Seed once from an existing complete G7 test profile; never merge/overwrite
    # saves on subsequent updates. Original profiles remain available for rollback.
    $source = Get-ChildItem -LiteralPath (Join-Path $repo '.local') -Directory -Filter 'virtual-player-mining-host*' |
        Where-Object { @(Get-ChildItem -LiteralPath (Join-Path $_.FullName 'Players') -File -Filter '*.plr' -ErrorAction SilentlyContinue).Count -gt 0 `
            -and @(Get-ChildItem -LiteralPath (Join-Path $_.FullName 'Worlds') -File -Filter '*.wld' -ErrorAction SilentlyContinue).Count -gt 0 } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($source) {
        New-Item -ItemType Directory -Path $profile | Out-Null
        foreach ($entry in @('Players', 'Worlds', 'VirtualPlayerEngineProbe', 'config.json', 'input profiles.json', 'favorites.json')) {
            $sourceEntry = Join-Path $source.FullName $entry
            if (Test-Path -LiteralPath $sourceEntry) { Copy-Item -LiteralPath $sourceEntry -Destination $profile -Recurse }
        }
        Set-Content -LiteralPath (Join-Path $profile 'IMPORTED_FROM.txt') -Value $source.FullName
        Write-Output ('Reused existing characters/worlds from: ' + $source.FullName)
    }
}
New-Item -ItemType Directory -Path (Join-Path $profile 'Mods') -Force | Out-Null
Copy-Item -LiteralPath $package -Destination (Join-Path $profile 'Mods/VirtualPlayerEngineProbe.tmod')
$enabledPath = Join-Path $profile 'Mods/enabled.json'
if (!(Test-Path -LiteralPath $enabledPath)) { Set-Content -LiteralPath $enabledPath -Value '["VirtualPlayerEngineProbe"]' }
$launcher = @'
param([string]$TModLoaderPath = '__TML__')
$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'Mods/VirtualPlayerEngineProbe.tmod'
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '__HASH__') { throw 'Integrated package changed; build and verify before preparing a new profile.' }
$enabled = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Mods/enabled.json') -Raw | ConvertFrom-Json
$extra = @($enabled | Where-Object { $_ -notin @('VirtualPlayerEngineProbe', 'ModLoader') })
if ($extra.Count) { throw ('Probe isolation: disable these mods before launch: ' + ($extra -join ', ')) }
$listeners = @(Get-NetTCPConnection -LocalPort 7777 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count) { throw ('Host & Play port 7777 is already in use (PID ' + (($listeners.OwningProcess | Select-Object -Unique) -join ', ') + '). Close the previous server before retrying.') }
Push-Location $TModLoaderPath
try { & (Join-Path $TModLoaderPath 'start-tModLoader.bat') -tmlsavedirectory $PSScriptRoot -modpath (Join-Path $PSScriptRoot 'Mods') }
finally { Pop-Location }
'@
Set-Content -LiteralPath (Join-Path $profile 'start-host-play.ps1') -Value $launcher.Replace('__HASH__', $packageHash).Replace('__TML__', $TModLoaderPath.Replace("'", "''"))
Set-Content -LiteralPath (Join-Path $profile 'BUILD.txt') -Value @(('VirtualPlayerEngineProbe '+$paths.Version+' (Single-window integration)'), ('SHA256=' + $packageHash),
    'See VIRTUAL_PLAYER_SINGLE_WINDOW.md for actual offline/native validation results. CURRENT HOST AND PLAY NOT RUN; REMOTE MULTIPLAYER NOT RUN.',
    'Run start-host-play.ps1 manually; reuse the existing test character/world; Multiplayer > Host & Play.',
    '/vph load (or spawn), /vph item-fixture, /vph slot <reported number>, /vph mine <tileX> <tileY>, /vph mine-status, /vph mine-stop; current selected real pickaxe.')
Write-Output ('Prepared: ' + $profile)
Write-Output ('SHA256: ' + $packageHash)
Write-Output ('Manual launch: ' + (Join-Path $profile 'start-host-play.ps1'))
