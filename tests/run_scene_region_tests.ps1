$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=Join-Path $repo '.local/m4-5-offline'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$baseline=& git -C $repo show 'd8e98e1:CompanionBridge/ServerIpc/ServerSceneReader.cs'
if($LASTEXITCODE -ne 0){throw 'M4.4 baseline is required.'}
$baselinePath=Join-Path $output 'BaselineServerSceneReader.cs'
($baseline -join "`n").Replace('class ServerSceneReader','class BaselineServerSceneReader') | Set-Content -LiteralPath $baselinePath -Encoding utf8
$sources=@('CompanionBridge/SceneLayers.cs','CompanionBridge/AtomicParameters.cs','CompanionBridge/SceneGeometry.cs','CompanionBridge/ScenePassages.cs','CompanionBridge/SceneRegion.cs',
    'CompanionBridge/ObservationSnapshot.cs','CompanionBridge/TerrainSnapshot.cs','CompanionBridge/EntitySnapshot.cs','CompanionBridge/HotbarSlots.cs',
    'CompanionBridge/Motion/PhysicsSimulator.cs','CompanionBridge/Motion/MotionSupport.cs','CompanionBridge/ServerIpc/ServerSceneReader.cs',
    'tests/SceneReaderLayerStubs.cs','tests/SceneRegionTests.cs') | ForEach-Object {Join-Path $repo $_}
$refs=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path @($sources+$baselinePath) -ReferencedAssemblies $refs
[SceneRegionTests]::Run($output)
