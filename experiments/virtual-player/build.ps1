param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $repo '.local/virtual-player-build'
# Build/package into a new isolated savedirectory. Never deploy to a live profile.
Push-Location $TModLoaderPath
try {
    & (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') (Join-Path $TModLoaderPath 'tModLoader.dll') `
        -server -build (Join-Path $PSScriptRoot 'VirtualPlayerProbe') -tmlsavedirectory $output
    if ($LASTEXITCODE -ne 0) { throw "VirtualPlayerProbe build failed: $LASTEXITCODE" }
    if (!(Test-Path (Join-Path $output 'Mods/VirtualPlayerProbe.tmod'))) { throw 'Package not produced' }
} finally { Pop-Location }
