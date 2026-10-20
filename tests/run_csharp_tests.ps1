param([switch]$MotionOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# PowerShell 7.4+ uses .NET 8+; compile the actual IPC sources without Terraria.
$sources = @(
    (Join-Path $repo 'CompanionBridge/ControlState.cs'),
    (Join-Path $repo 'CompanionBridge/TerrainSnapshot.cs'),
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
if (!$MotionOnly) { [BridgeOfflineTests]::Run() }
[MotionOfflineTests]::Run()
