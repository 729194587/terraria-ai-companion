param([switch]$MotionOnly, [string]$ObservationSamplePath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# PowerShell 7.4+ uses .NET 8+; compile the actual IPC sources without Terraria.
$sources = @(
    (Join-Path $repo 'CompanionBridge/ControlState.cs'),
    (Join-Path $repo 'CompanionBridge/GameMessageCapture.cs'),
    (Join-Path $PSScriptRoot 'GameMessageOfflineTests.cs'),
    (Join-Path $repo 'CompanionBridge/ObservationSnapshot.cs'),
    (Join-Path $PSScriptRoot 'ObservationOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'DeathLifecycleOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'BoundedMovementOfflineTests.cs'),
    (Join-Path $repo 'CompanionBridge/TerrainSnapshot.cs'),
    (Join-Path $repo 'CompanionBridge/EntitySnapshot.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/PhysicsSimulator.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MotionSupport.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/LocalMotionController.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/StateSpacePlanner.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationTask.cs'),
    (Join-Path $repo 'CompanionBridge/TcpControlServer.cs'),
    (Join-Path $PSScriptRoot 'BridgeOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'MotionOfflineTests.cs')
)
$references = Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies $references
if (!$MotionOnly) { [BridgeOfflineTests]::Run(); [ObservationOfflineTests]::Run(); [DeathLifecycleOfflineTests]::Run(); [GameMessageOfflineTests]::Run(); [BoundedMovementOfflineTests]::Run() }
if ($ObservationSamplePath -and !$MotionOnly) {
    [System.IO.File]::WriteAllText($ObservationSamplePath, [ObservationOfflineTests]::SampleJson,
        [System.Text.UTF8Encoding]::new($false))
}
[MotionOfflineTests]::Run()
