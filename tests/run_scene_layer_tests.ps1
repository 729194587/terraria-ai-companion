$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=Join-Path $repo '.local/scene-compact-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Pin both reader AND serialized records, so new record fields cannot contaminate the baseline.
$baseline=& git -C $repo show 'cff5ae7:CompanionBridge/ServerIpc/ServerSceneReader.cs'
if($LASTEXITCODE -ne 0){throw 'Pinned cff5ae7 baseline is required for compatibility checks.'}
$geometry=& git -C $repo show 'cff5ae7:CompanionBridge/SceneGeometry.cs'
if($LASTEXITCODE -ne 0){throw 'Pinned cff5ae7 geometry is required.'}
$oldGeometry=$geometry -join "`n"
$oldReader=($baseline -join "`n").Replace('class ServerSceneReader','class BaselineServerSceneReader')
$types=[regex]::Matches($oldGeometry,'\b(?:record|class) (Scene\w+)') | ForEach-Object {$_.Groups[1].Value} | Sort-Object -Unique
foreach($type in $types){$oldGeometry=[regex]::Replace($oldGeometry,"\b$type\b",('Baseline'+$type));$oldReader=[regex]::Replace($oldReader,"\b$type\b",('Baseline'+$type))}
$baselinePath=Join-Path $output 'BaselineServerSceneReader.cs'
$baselineGeometryPath=Join-Path $output 'BaselineSceneGeometry.cs'
$oldReader | Set-Content -LiteralPath $baselinePath -Encoding utf8
$oldGeometry | Set-Content -LiteralPath $baselineGeometryPath -Encoding utf8
$sources=@('CompanionBridge/SceneLayers.cs','CompanionBridge/AtomicParameters.cs','CompanionBridge/SceneGeometry.cs','CompanionBridge/ScenePassages.cs','CompanionBridge/SceneRegion.cs',
    'CompanionBridge/ObservationSnapshot.cs','CompanionBridge/TerrainSnapshot.cs','CompanionBridge/EntitySnapshot.cs','CompanionBridge/HotbarSlots.cs',
    'CompanionBridge/Motion/PhysicsSimulator.cs','CompanionBridge/Motion/MotionSupport.cs','CompanionBridge/ServerIpc/ServerSceneReader.cs',
    'tests/SceneReaderLayerStubs.cs','tests/SceneReaderLayerTests.cs') | ForEach-Object {Join-Path $repo $_}
$references=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path @($sources+$baselinePath+$baselineGeometryPath) -ReferencedAssemblies $references
[SceneReaderLayerTests]::Run($output)
