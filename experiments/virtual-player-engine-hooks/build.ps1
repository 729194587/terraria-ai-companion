param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
$repo=$paths.Repository
$output = $paths.Build
Push-Location $TModLoaderPath
try {
    & (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') (Join-Path $TModLoaderPath 'tModLoader.dll') `
        -server -build (Join-Path $PSScriptRoot 'VirtualPlayerEngineProbe') -tmlsavedirectory $output
    if ($LASTEXITCODE -ne 0) { throw "Engine probe build failed: $LASTEXITCODE" }
    if (!(Test-Path (Join-Path $output 'Mods/VirtualPlayerEngineProbe.tmod'))) { throw 'Probe package not produced' }
} finally { Pop-Location }
