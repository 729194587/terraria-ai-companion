param([string]$RestoreRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$out=Join-Path $repo ('.local/ai-map-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$sources=@('CompanionBridge/ServerPlayerRuntime/AiMap.cs','CompanionBridge/ServerPlayerRuntime/CharacterFiles.cs','CompanionBridge/ServerPlayerRuntime/WorldCharacterFiles.cs','tests/AiMapTests.cs')|ForEach-Object{Join-Path $repo $_}
$refs=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies $refs
if($RestoreRoot){[AiMapTests]::Restore($RestoreRoot);return}
[AiMapTests]::Run($out)
& pwsh -NoProfile -File $PSCommandPath -RestoreRoot $out
if($LASTEXITCODE -ne 0){throw "Cross-process map restore failed"}
