# Current integration artifacts only. Historical profiles and packages are retained.
function Get-ProbePaths {
    $repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $build=Join-Path $repository '.local/virtual-player-single-window-build'
    $versionLine=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VirtualPlayerEngineProbe/build.txt') |
        Where-Object {$_ -match '^version\s*='}
    if(@($versionLine).Count -ne 1){throw 'Expected one probe build version.'}
    [pscustomobject]@{
        Repository=$repository
        Build=$build
        Package=Join-Path $build 'Mods/VirtualPlayerEngineProbe.tmod'
        Validation=Join-Path $repository '.local/virtual-player-single-window-validation'
        Host=Join-Path $repository '.local/virtual-player-single-window-host'
        Backups=Join-Path $repository '.local/virtual-player-single-window-backups'
        Version=($versionLine -split '=',2)[1].Trim()
    }
}
