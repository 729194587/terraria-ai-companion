param([Parameter(Mandatory)][string]$ReadyPath,[Parameter(Mandatory)][string]$StopPath)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sources=@('CompanionBridge/HotbarSlots.cs','CompanionBridge/AtomicParameters.cs','CompanionBridge/SceneLayers.cs',
 'CompanionBridge/ServerIpc/AiReplyFence.cs','CompanionBridge/ServerIpc/AiAtomicActions.cs',
 'CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs','CompanionBridge/ServerIpc/AiReadOnlyServer.cs',
 'tests/AtomicParameterContractTests.cs','tests/ApiContractProtocolHost.cs') | ForEach-Object {Join-Path $repo $_}
$references=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies $references
[ApiContractProtocolHost]::Run($ReadyPath,$StopPath)
