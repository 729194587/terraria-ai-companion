param([string]$PackagePath,[string]$TModLoaderPath,[string]$EngineProfile)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sources=@('CompanionBridge/HotbarSlots.cs','CompanionBridge/ServerIpc/AiReplyFence.cs','CompanionBridge/ServerIpc/AiAtomicActions.cs','CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs','CompanionBridge/ServerIpc/AiReadOnlyServer.cs','tests/ServerAiIpcOfflineTests.cs','tests/ServerAiAtomicOfflineTests.cs','tests/ServerAiReadProtocolTests.cs') | ForEach-Object { Join-Path $repo $_ }
$references=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
$sources+=@('CompanionBridge/AtomicParameters.cs','CompanionBridge/SceneLayers.cs','tests/AtomicParameterContractTests.cs') | ForEach-Object {Join-Path $repo $_}
if($PackagePath){
    . (Join-Path $repo 'scripts/engine-profile.ps1')
    $selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
    Assert-CompanionPackage -Package $PackagePath -Engine $selectedEngine
    $cecil=Join-Path $selectedEngine.Path 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
    Add-Type -Path $cecil
    $references+=@($cecil)
    $sources+=@(Join-Path $repo 'tests/ServerAiPackageTests.cs')
}
$sources+=@(Join-Path $repo 'tests/ServerAiNativeActionOfflineTests.cs')
$sources+=@(Join-Path $repo 'tests/ServerAiChatProtocolTests.cs')
$sources+=@(Join-Path $repo 'CompanionBridge/ServerIpc/AiActionReasons.cs')
$sources+=@('CompanionBridge/ServerIpc/TaskRuntime.cs','CompanionBridge/ServerIpc/MineTargetTask.cs','tests/TaskRuntimeOfflineTests.cs') | ForEach-Object {Join-Path $repo $_}
Add-Type -Path $sources -ReferencedAssemblies $references
[ServerAiIpcOfflineTests]::Run()
[ServerAiAtomicOfflineTests]::Run()
[ServerAiReadProtocolTests]::Run()
[ServerAiNativeActionOfflineTests]::Run()
[TaskRuntimeOfflineTests]::Run()
[ServerAiChatProtocolTests]::Run()
if($PackagePath){
    $version=(Select-String -LiteralPath (Join-Path $repo 'CompanionBridge/build.txt') -Pattern '^version\s*=\s*(\S+)$').Matches[0].Groups[1].Value
    [ServerAiPackageTests]::Run((Resolve-Path -LiteralPath $PackagePath).Path,$version)
}
