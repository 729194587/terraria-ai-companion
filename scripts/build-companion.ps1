param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=if($OutputPath){[IO.Path]::GetFullPath($OutputPath)}else{Join-Path $repo '.local/companion-g82-build'}
Push-Location $TModLoaderPath
try {
    & (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') (Join-Path $TModLoaderPath 'tModLoader.dll') -server -build (Join-Path $repo 'CompanionBridge') -tmlsavedirectory $output
    if($LASTEXITCODE -ne 0){throw "CompanionBridge build failed: $LASTEXITCODE"}
    if(!(Test-Path -LiteralPath (Join-Path $output 'Mods/CompanionBridge.tmod'))){throw 'CompanionBridge package missing'}
} finally { Pop-Location }
