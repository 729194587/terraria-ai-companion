param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[switch]$Fresh)
$ErrorActionPreference='Stop'
Write-Output 'Current integrated profile: forwarding to prepare-host-play.ps1; old mining profiles are retained.'
& (Join-Path $PSScriptRoot 'prepare-host-play.ps1') -TModLoaderPath $TModLoaderPath -Fresh:$Fresh
