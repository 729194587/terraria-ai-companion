$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=Join-Path $repo '.local/scene-layer-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Pin the real pre-layers reader for default compatibility; never edit the checkout.
$baseline=& git -C $repo show '61bf2d7:CompanionBridge/ServerIpc/ServerSceneReader.cs'
if($LASTEXITCODE -ne 0){throw 'Pinned 61bf2d7 baseline is required for compatibility checks.'}
$baselinePath=Join-Path $output 'BaselineServerSceneReader.cs'
($baseline -join "`n").Replace('class ServerSceneReader','class BaselineServerSceneReader') | Set-Content -LiteralPath $baselinePath -Encoding utf8
$sources=@('CompanionBridge/SceneLayers.cs','CompanionBridge/AtomicParameters.cs','CompanionBridge/SceneGeometry.cs',
    'CompanionBridge/ObservationSnapshot.cs','CompanionBridge/TerrainSnapshot.cs','CompanionBridge/EntitySnapshot.cs','CompanionBridge/HotbarSlots.cs',
    'CompanionBridge/Motion/PhysicsSimulator.cs','CompanionBridge/Motion/MotionSupport.cs','CompanionBridge/ServerIpc/ServerSceneReader.cs',
    'tests/SceneReaderLayerStubs.cs','tests/SceneReaderLayerTests.cs') | ForEach-Object {Join-Path $repo $_}
$references=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path @($sources+$baselinePath) -ReferencedAssemblies $references
[SceneReaderLayerTests]::Run($output)
