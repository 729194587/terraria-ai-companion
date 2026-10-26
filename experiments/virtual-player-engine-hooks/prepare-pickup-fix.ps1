param([Parameter(Mandatory)][string]$SourceProfile,[Parameter(Mandatory)][string]$ProfilePath,
    [string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
$repo=$paths.Repository
$package=$paths.Package
if(!(Test-Path -LiteralPath $package)){throw 'Build and verify the pickup fix first.'}
$SourceProfile=(Resolve-Path -LiteralPath $SourceProfile).Path
$ProfilePath=[IO.Path]::GetFullPath($ProfilePath)
if(Test-Path -LiteralPath $ProfilePath){throw 'Destination exists; use a new independent profile instead of overwriting saves.'}
if(!(Get-ChildItem -LiteralPath (Join-Path $SourceProfile 'Players') -Filter '*.plr') -or
    !(Get-ChildItem -LiteralPath (Join-Path $SourceProfile 'Worlds') -Filter '*.wld')){throw 'Source must contain an existing test character and world.'}
$hash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $ProfilePath|Out-Null
foreach($entry in @('Players','Worlds','VirtualPlayerEngineProbe','config.json','input profiles.json','favorites.json')){
    $sourceEntry=Join-Path $SourceProfile $entry
    if(Test-Path -LiteralPath $sourceEntry){Copy-Item -LiteralPath $sourceEntry -Destination $ProfilePath -Recurse}
}
New-Item -ItemType Directory -Path (Join-Path $ProfilePath 'Mods')|Out-Null
Copy-Item -LiteralPath $package -Destination (Join-Path $ProfilePath 'Mods/VirtualPlayerEngineProbe.tmod')
Set-Content -LiteralPath (Join-Path $ProfilePath 'Mods/enabled.json') -Value '["VirtualPlayerEngineProbe"]'
$launcher=@'
param([string]$TModLoaderPath='__TML__')
$ErrorActionPreference='Stop'
$package=Join-Path $PSScriptRoot 'Mods/VirtualPlayerEngineProbe.tmod'
if((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne '__HASH__'){throw 'Pickup-fix package hash changed.'}
$extra=@((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Mods/enabled.json') -Raw|ConvertFrom-Json)|Where-Object {$_ -notin @('VirtualPlayerEngineProbe','ModLoader')})
if($extra.Count){throw 'Disable other mods in this isolated profile.'}
if(@(Get-NetTCPConnection -LocalPort 7777 -State Listen -ErrorAction SilentlyContinue).Count){throw 'Close the previous Host & Play server before launching this profile.'}
Push-Location $TModLoaderPath
try{& (Join-Path $TModLoaderPath 'start-tModLoader.bat') -tmlsavedirectory $PSScriptRoot -modpath (Join-Path $PSScriptRoot 'Mods')}
finally{Pop-Location}
'@
Set-Content -LiteralPath (Join-Path $ProfilePath 'start-host-play.ps1') -Value $launcher.Replace('__TML__',$TModLoaderPath.Replace("'","''")).Replace('__HASH__',$hash)
Set-Content -LiteralPath (Join-Path $ProfilePath 'BUILD.txt') -Value @(('Current integrated '+$paths.Version+'; G7 mining + pickup ownership fix'),"SHA256=$hash",'Native server baseline regression passed; graphical and remote multiplayer retest pending.',"COPIED_FROM=$SourceProfile",'/vph load; approach drops formerly closest to AI, then approach again after moving farther from AI.')
Write-Output "Prepared independent pickup-fix profile: $ProfilePath"
Write-Output "SHA256: $hash"
