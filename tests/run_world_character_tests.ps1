$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$out=Join-Path $repo ('.local/world-character-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$sources=@('CompanionBridge/ServerPlayerRuntime/CharacterFiles.cs','CompanionBridge/ServerPlayerRuntime/WorldCharacterFiles.cs','tests/WorldCharacterFilesTests.cs')|ForEach-Object{Join-Path $repo $_}
$refs=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies $refs
[WorldCharacterFilesTests]::Run($out)
Write-Output "Evidence: $out"
