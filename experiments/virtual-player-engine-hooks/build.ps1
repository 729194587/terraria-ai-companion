param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
$repo=$paths.Repository
$output = $paths.Build
$staging = Join-Path $output 'source/VirtualPlayerEngineProbe'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'VirtualPlayerEngineProbe/*') -Destination $staging -Recurse -Force
Copy-Item -Path (Join-Path $repo 'CompanionBridge/ServerPlayerRuntime/*.cs') -Destination $staging -Force
Push-Location $TModLoaderPath
try {
    & (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') (Join-Path $TModLoaderPath 'tModLoader.dll') `
        -server -build $staging -tmlsavedirectory $output
    if ($LASTEXITCODE -ne 0) { throw "Engine probe build failed: $LASTEXITCODE" }
    if (!(Test-Path (Join-Path $output 'Mods/VirtualPlayerEngineProbe.tmod'))) { throw 'Probe package not produced' }
} finally { Pop-Location }
