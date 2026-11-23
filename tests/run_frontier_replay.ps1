$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sources=@('CompanionBridge/ServerPlayerRuntime/AiMap.cs','CompanionBridge/ServerPlayerRuntime/ExplorationFrontiers.cs',
    'CompanionBridge/ServerPlayerRuntime/ExplorationCostmap.cs','CompanionBridge/ServerPlayerRuntime/ExplorationTrip.cs','tests/FrontierReplayTests.cs')
Add-Type -Path ($sources | ForEach-Object {Join-Path $repo $_}) -ReferencedAssemblies (Get-ChildItem (Join-Path $PSHOME ref) -Filter '*.dll').FullName
[FrontierReplayTests]::Run()
