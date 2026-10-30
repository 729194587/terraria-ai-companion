param([switch]$SafeGroundWalkOnly, [switch]$MiningSupportOnly, [switch]$MotionOnly, [string]$ObservationSamplePath, [switch]$LandingBudgetAudit,
    [int]$BudgetRepetitions = 100, [switch]$BudgetColdOnly, [switch]$LandingProtocolHost, [switch]$LandingOnly, [switch]$AuthorityNetworkOnly,
    [switch]$InventoryOnly, [switch]$BridgeOnly, [switch]$ServerReadsOnly, [switch]$NavigationOnly,[switch]$SceneOnly,[switch]$VerticalOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# PowerShell 7.4+ uses .NET 8+; compile the actual IPC sources without Terraria.
$sources = @(
    (Join-Path $repo 'CompanionBridge/AiChatInput.cs'),
    (Join-Path $PSScriptRoot 'AiChatOfflineTests.cs'),
    (Join-Path $repo 'CompanionBridge/AtomicParameters.cs'),
    (Join-Path $repo 'CompanionBridge/SceneLayers.cs'),
    (Join-Path $repo 'CompanionBridge/AtomicAuthority.cs'),
    (Join-Path $repo 'CompanionBridge/Common/Systems/NativeAuthorityWorld.cs'),
    (Join-Path $PSScriptRoot 'AtomicAuthorityOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'AuthorityNetworkStubs.cs'),
    (Join-Path $repo 'CompanionBridge/HotbarSlots.cs'),
    (Join-Path $repo 'CompanionBridge/ItemAim.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/PlatformDropAction.cs'),
    (Join-Path $PSScriptRoot 'AtomicOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'PlatformDropOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'LandingNavigationOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'AtomicGameOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'AtomicExtensionsOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'AtomicFoundationsOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'InventoryOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'ServerAiSharedReadTests.cs'),
    (Join-Path $PSScriptRoot 'LocalInputOfflineTests.cs'),
    (Join-Path $repo 'CompanionBridge/Common/Systems/AtomicOperations.cs'),
    (Join-Path $repo 'CompanionBridge/Common/Systems/AtomicInventory.cs'),
    (Join-Path $repo 'CompanionBridge/Common/Systems/AtomicQueries.cs'),
    (Join-Path $repo 'CompanionBridge/ControlState.cs'),
    (Join-Path $repo 'CompanionBridge/GameMessageCapture.cs'),
    (Join-Path $PSScriptRoot 'GameMessageOfflineTests.cs'),
    (Join-Path $repo 'CompanionBridge/ObservationSnapshot.cs'),
    (Join-Path $PSScriptRoot 'ObservationOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'DeathLifecycleOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'BoundedMovementOfflineTests.cs'),
    (Join-Path $repo 'CompanionBridge/TerrainSnapshot.cs'),
    (Join-Path $repo 'CompanionBridge/SceneGeometry.cs'),
    (Join-Path $PSScriptRoot 'SceneGeometryTests.cs'),
    (Join-Path $repo 'CompanionBridge/EntitySnapshot.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/PhysicsSimulator.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationBuffPolicy.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MotionSupport.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MiningSupportImpact.cs'),
    (Join-Path $PSScriptRoot 'MiningSupportImpactTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/LocalMotionController.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/StateSpacePlanner.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/SafeGroundWalk.cs'),
    (Join-Path $PSScriptRoot 'SafeGroundWalkTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationTask.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationDiagnostics.cs'),
    (Join-Path $repo 'CompanionBridge/TcpControlServer.cs'),
    (Join-Path $PSScriptRoot 'BridgeOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'MotionOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'VerticalNavigationTests.cs'),
    (Join-Path $PSScriptRoot 'NavigationLandingConfirmationTests.cs'),
    (Join-Path $PSScriptRoot 'NavigationPerceptionTests.cs'),
    (Join-Path $repo 'CompanionBridge/Common/Players/CompanionPlayer.cs'),
    (Join-Path $PSScriptRoot 'PlayerHookOfflineTests.cs')
)
$references = Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
if ($AuthorityNetworkOnly) {
    $sources += (Join-Path $repo 'CompanionBridge/Common/Systems/AtomicMultiplayer.cs')
    $sources += (Join-Path $PSScriptRoot 'AuthorityNetworkOfflineTests.cs')
    Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/define:ATOMIC_NETWORK_TESTS'
    [AuthorityNetworkOfflineTests]::Run()
    return
}
Add-Type -Path $sources -ReferencedAssemblies $references
if ($SafeGroundWalkOnly) { [SafeGroundWalkTests]::Run(); return }
if ($MiningSupportOnly) { [MiningSupportImpactTests]::Run((Join-Path $PSScriptRoot 'fixtures/mining-support-play2.json')); return }
if ($SceneOnly) { [SceneGeometryTests]::Run(); return }
if ($VerticalOnly) { [VerticalNavigationTests]::Run(); return }
if ($NavigationOnly) { [NavigationPerceptionTests]::Run(); [NavigationLandingConfirmationTests]::Run(); [MotionOfflineTests]::Run(); [PlayerHookOfflineTests]::Run(); return }
if ($ServerReadsOnly) { [ServerAiSharedReadTests]::Run(); [AtomicExtensionsOfflineTests]::Run(); return }
if ($BridgeOnly) { [AiChatOfflineTests]::Run(); [BridgeOfflineTests]::Run(); [PlayerHookOfflineTests]::Run(); [LocalInputOfflineTests]::Run(); [DeathLifecycleOfflineTests]::Run(); return }
if ($InventoryOnly) { [InventoryOfflineTests]::Run(); return }
if ($LandingProtocolHost) { [LandingNavigationOfflineTests]::RunProtocolHost(); return }
if ($LandingOnly) { [LandingNavigationOfflineTests]::Run(); return }
if ($LandingBudgetAudit) { [MotionOfflineTests]::AuditLandingBudget($BudgetRepetitions, $BudgetColdOnly.IsPresent); return }
if (!$MotionOnly) { [AtomicOfflineTests]::Run(); [BridgeOfflineTests]::Run(); [ObservationOfflineTests]::Run(); [DeathLifecycleOfflineTests]::Run(); [GameMessageOfflineTests]::Run(); [BoundedMovementOfflineTests]::Run() }
if ($ObservationSamplePath -and !$MotionOnly) {
    [System.IO.File]::WriteAllText($ObservationSamplePath, [ObservationOfflineTests]::SampleJson,
        [System.Text.UTF8Encoding]::new($false))
}
[MotionOfflineTests]::Run()
[PlayerHookOfflineTests]::Run()
[AtomicGameOfflineTests]::Run()
[AtomicExtensionsOfflineTests]::Run()
[AtomicFoundationsOfflineTests]::Run()
[InventoryOfflineTests]::Run()
[LocalInputOfflineTests]::Run()
[AtomicAuthorityOfflineTests]::Run()

[LandingNavigationOfflineTests]::Run()

[PlatformDropOfflineTests]::Run()
