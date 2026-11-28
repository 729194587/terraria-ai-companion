param([switch]$AutonomyChatOnly,[switch]$LiquidOnly,[switch]$WorldJourneyOnly,[switch]$GrappleOnly,[switch]$AirOnly,[switch]$MobilityOnly,[switch]$TripOnly,[switch]$RecoveryOnly, [switch]$PlannerOnly, [switch]$SafeGroundWalkOnly, [switch]$MiningSupportOnly, [switch]$MotionOnly, [string]$ObservationSamplePath, [switch]$LandingBudgetAudit,
    [int]$BudgetRepetitions = 100, [switch]$BudgetColdOnly, [switch]$LandingProtocolHost, [switch]$LandingOnly, [switch]$AuthorityNetworkOnly,
    [switch]$InventoryOnly, [switch]$BridgeOnly, [switch]$ServerReadsOnly, [switch]$NavigationOnly,[switch]$SurfaceGraphOnly,[switch]$LocalEditsOnly,[switch]$SceneOnly,[switch]$VerticalOnly,[string]$NavigationExperimentOutput)
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
    (Join-Path $PSScriptRoot 'LiquidTerrainTests.cs'),
    (Join-Path $repo 'CompanionBridge/SceneGeometry.cs'),
    (Join-Path $repo 'CompanionBridge/ScenePassages.cs'),
    (Join-Path $repo 'CompanionBridge/SceneRegion.cs'),
    (Join-Path $PSScriptRoot 'SceneGeometryTests.cs'),
    (Join-Path $repo 'CompanionBridge/EntitySnapshot.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/PhysicsSimulator.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/ExtraJumpModel.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/AirMotionModel.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/TravelHealth.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MobilityProfile.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MovementSnapshot.cs'),
    (Join-Path $PSScriptRoot 'MovementSnapshotTests.cs'),
    (Join-Path $PSScriptRoot 'ExtraJumpNavigationTests.cs'),
    (Join-Path $PSScriptRoot 'AirMotionTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MotionSupport.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/MiningSupportImpact.cs'),
    (Join-Path $PSScriptRoot 'MiningSupportImpactTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/LocalMotionController.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/StateSpacePlanner.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/PoolCrossing.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/SafeGroundWalk.cs'),
    (Join-Path $PSScriptRoot 'SafeGroundWalkTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationTask.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationFailureMemory.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationKnowledge.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/SurfaceGraph.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/GrappleClimbPlan.cs'),
    (Join-Path $PSScriptRoot 'GrappleClimbTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/LocalTerrainPlanning.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/OverlayMotionWorld.cs'),
    (Join-Path $PSScriptRoot 'LocalTerrainPlanningTests.cs'),
    (Join-Path $PSScriptRoot 'SurfaceGraphTests.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationProgressWatchdog.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationTripProgress.cs'),
    (Join-Path $repo 'CompanionBridge/Motion/NavigationDiagnostics.cs'),
    (Join-Path $repo 'CompanionBridge/TcpControlServer.cs'),
    (Join-Path $PSScriptRoot 'BridgeOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'MotionOfflineTests.cs'),
    (Join-Path $PSScriptRoot 'PlannerCorrectnessTests.cs'),
    (Join-Path $PSScriptRoot 'VerticalNavigationTests.cs'),
    (Join-Path $PSScriptRoot 'NavigationLandingConfirmationTests.cs'),
    (Join-Path $PSScriptRoot 'NavigationRecoveryTests.cs'),
    (Join-Path $PSScriptRoot 'NavigationTripTests.cs'),
    (Join-Path $PSScriptRoot 'MobilityProfileTests.cs'),
    (Join-Path $PSScriptRoot 'NavigationPerceptionTests.cs'),
    (Join-Path $repo 'CompanionBridge/Common/Players/CompanionPlayer.cs'),
    (Join-Path $PSScriptRoot 'PlayerHookOfflineTests.cs')
)
$references = Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
if ($SceneOnly -or $AutonomyChatOnly -or $MobilityOnly -or $VerticalOnly -or $LocalEditsOnly -or $SafeGroundWalkOnly -or $LiquidOnly -or $WorldJourneyOnly -or $TripOnly -or $GrappleOnly -or $AirOnly -or $PlannerOnly -or $NavigationExperimentOutput -or $SurfaceGraphOnly -or $LandingBudgetAudit) {
    $dependencies = @('AirMotionTests','PlannerCorrectnessTests','MotionOfflineTests','SurfaceGraphTests','ControlState','AtomicParameters',
        'SceneLayers','AiChatInput','HotbarSlots','GameMessageCapture','ObservationSnapshot','TerrainSnapshot',
        'SceneGeometry','SceneRegion','EntitySnapshot')
    if ($AutonomyChatOnly) { $dependencies += 'TcpControlServer','AiChatOfflineTests' }
    if ($MobilityOnly) { $dependencies += 'MobilityProfileTests','ExtraJumpNavigationTests','MovementSnapshotTests' }
    if ($VerticalOnly) { $dependencies += 'VerticalNavigationTests' }
    if ($SceneOnly) { $dependencies += 'SceneGeometryTests','ScenePassages' }
    if ($WorldJourneyOnly -or $TripOnly) { $dependencies += 'NavigationTripTests' }
    if ($LocalEditsOnly) { $dependencies += 'LocalTerrainPlanningTests' }
    if ($SafeGroundWalkOnly) { $dependencies += 'SafeGroundWalkTests' }
    if ($LiquidOnly) { $dependencies += 'LiquidTerrainTests' }
    if ($GrappleOnly) { $dependencies += 'GrappleClimbTests' }
    $motionSources = $sources | Where-Object {
        ((Split-Path (Split-Path $_ -Parent) -Leaf) -eq 'Motion' -and [IO.Path]::GetFileNameWithoutExtension($_) -ne 'PlatformDropAction') -or [IO.Path]::GetFileNameWithoutExtension($_) -in $dependencies
    }
    if ($MobilityOnly -or $AutonomyChatOnly) {
        $motionSources += Join-Path $repo 'CompanionBridge/Motion/NavigationAirActions.cs'
        $motionSources += Join-Path $PSScriptRoot 'AirActionFaultTests.cs'
    }
    if ($NavigationExperimentOutput) {
        $motionSources += Join-Path $PSScriptRoot 'navigation_experiments/NavigationExperiment.cs'
        Add-Type -Path $motionSources -ReferencedAssemblies $references -CompilerOptions '/define:NAVIGATION_EXPERIMENT','/optimize+'
        [CompanionBridge.Motion.NavigationExperiment]::Run($NavigationExperimentOutput)
        return
    }
    Add-Type -Path $motionSources -ReferencedAssemblies $references
    if ($SceneOnly) { [SceneGeometryTests]::Run(); return }
    if ($AutonomyChatOnly) { [AiChatOfflineTests]::Run(); return }
    if ($MobilityOnly) { [AirActionFaultTests]::Run(); [MobilityProfileTests]::Run(); [ExtraJumpNavigationTests]::Run(); [AirMotionTests]::Run() }
    elseif ($PlannerOnly) { [PlannerCorrectnessTests]::Run() }
    elseif ($VerticalOnly) { [VerticalNavigationTests]::Run() }
    elseif ($LocalEditsOnly) { [LocalTerrainPlanningTests]::Run() }
    elseif ($SafeGroundWalkOnly) { [SafeGroundWalkTests]::Run() }
    elseif ($LiquidOnly) { [LiquidTerrainTests]::Run() }
    elseif ($WorldJourneyOnly) { [NavigationTripTests]::WorldJourney() }
    elseif ($TripOnly) { [NavigationTripTests]::Run() }
    elseif ($GrappleOnly) { [GrappleClimbTests]::Run() }
    elseif ($AirOnly) { [AirMotionTests]::Run() }
    elseif ($SurfaceGraphOnly) { [SurfaceGraphTests]::Run() }
    else { [MotionOfflineTests]::AuditLandingBudget($BudgetRepetitions, $BudgetColdOnly.IsPresent) }
    return
}
$sources += Join-Path $repo 'CompanionBridge/Motion/NavigationAirActions.cs'
if ($AuthorityNetworkOnly) {
    $sources += (Join-Path $repo 'CompanionBridge/Common/Systems/AtomicMultiplayer.cs')
    $sources += (Join-Path $PSScriptRoot 'AuthorityNetworkOfflineTests.cs')
    Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/define:ATOMIC_NETWORK_TESTS'
    [AuthorityNetworkOfflineTests]::Run()
    return
}
Add-Type -Path $sources -ReferencedAssemblies $references
if ($RecoveryOnly) { [NavigationRecoveryTests]::Run(); return }
if ($MiningSupportOnly) { [MiningSupportImpactTests]::Run((Join-Path $PSScriptRoot 'fixtures/mining-support-play2.json')); return }
if ($NavigationOnly) { [LiquidTerrainTests]::Run(); [GrappleClimbTests]::Run(); [LocalTerrainPlanningTests]::Run(); [SurfaceGraphTests]::Run(); [MobilityProfileTests]::Run(); [ExtraJumpNavigationTests]::Run(); [AirMotionTests]::Run(); [NavigationTripTests]::Run(); [NavigationRecoveryTests]::Run(); [PlannerCorrectnessTests]::Run(); [NavigationPerceptionTests]::Run(); [NavigationLandingConfirmationTests]::Run(); [MotionOfflineTests]::Run(); [PlayerHookOfflineTests]::Run(); return }
if ($ServerReadsOnly) { [ServerAiSharedReadTests]::Run(); [AtomicExtensionsOfflineTests]::Run(); return }
if ($BridgeOnly) { [AiChatOfflineTests]::Run(); [BridgeOfflineTests]::Run(); [PlayerHookOfflineTests]::Run(); [LocalInputOfflineTests]::Run(); [DeathLifecycleOfflineTests]::Run(); return }
if ($InventoryOnly) { [InventoryOfflineTests]::Run(); return }
if ($LandingProtocolHost) { [LandingNavigationOfflineTests]::RunProtocolHost(); return }
if ($LandingOnly) { [LandingNavigationOfflineTests]::Run(); return }
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
