param([string]$TModLoaderPath,[string]$OutputPath,[string]$EngineProfile)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'engine-profile.ps1')
$engine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$engine.Path
$output=if($OutputPath){[IO.Path]::GetFullPath($OutputPath)}else{$engine.Output}
Push-Location $TModLoaderPath
try {
    & (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') (Join-Path $TModLoaderPath 'tModLoader.dll') -server -build (Join-Path $repo 'CompanionBridge') -tmlsavedirectory $output
    if($LASTEXITCODE -ne 0){throw "CompanionBridge build failed: $LASTEXITCODE"}
    $package=Join-Path $output 'Mods/CompanionBridge.tmod'
    Assert-CompanionPackage -Package $package -Engine $engine
    Write-Output "Built $($engine.Name): $package"
} finally { Pop-Location }
