param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $repo '.local/virtual-player-character-build'
Push-Location $TModLoaderPath
try {
    & (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') (Join-Path $TModLoaderPath 'tModLoader.dll') `
        -server -build (Join-Path $PSScriptRoot 'VirtualPlayerEngineProbe') -tmlsavedirectory $output
    if ($LASTEXITCODE -ne 0) { throw "Engine probe build failed: $LASTEXITCODE" }
    if (!(Test-Path (Join-Path $output 'Mods/VirtualPlayerEngineProbe.tmod'))) { throw 'Probe package not produced' }
} finally { Pop-Location }
