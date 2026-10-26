param([string]$PackagePath,[string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sources=@('CompanionBridge/HotbarSlots.cs','CompanionBridge/ServerIpc/AiAtomicActions.cs','CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs','CompanionBridge/ServerIpc/AiReadOnlyServer.cs','tests/ServerAiIpcOfflineTests.cs','tests/ServerAiAtomicOfflineTests.cs') | ForEach-Object { Join-Path $repo $_ }
$references=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
if($PackagePath){
    $cecil=Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
    Add-Type -Path $cecil
    $references+=@($cecil)
    $sources+=@(Join-Path $repo 'tests/ServerAiPackageTests.cs')
}
Add-Type -Path $sources -ReferencedAssemblies $references
[ServerAiIpcOfflineTests]::Run()
[ServerAiAtomicOfflineTests]::Run()
if($PackagePath){[ServerAiPackageTests]::Run((Resolve-Path -LiteralPath $PackagePath).Path)}
