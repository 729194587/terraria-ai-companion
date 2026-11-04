param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=Join-Path $repo '.local/server-ai-tool-checks'
$refsPath=Join-Path $output 'net80-refs'
New-Item -ItemType Directory -Path $refsPath -Force | Out-Null
$cecil=Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
$utils=Join-Path $TModLoaderPath 'Libraries/monomod.utils/25.0.10/lib/net8.0/MonoMod.Utils.dll'
$detour=Join-Path $TModLoaderPath 'Libraries/monomod.runtimedetour/25.3.2/lib/net8.0/MonoMod.RuntimeDetour.dll'
Add-Type -Path $cecil
$refPack=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $TModLoaderPath 'Libraries/basic.reference.assemblies.net80/1.5.0/lib/netstandard2.0/Basic.Reference.Assemblies.Net80.dll'))
try{
    foreach($resource in $refPack.MainModule.Resources){
        if($resource.Name.StartsWith('net80.')){[IO.File]::WriteAllBytes((Join-Path $refsPath ($resource.Name.Substring(6)+'.dll')),$resource.GetResourceData())}
    }
}finally{$refPack.Dispose()}
$sources=@('CompanionBridge/ServerPlayerRuntime/CraftingPatches.cs','tests/CraftingPatchOfflineTests.cs','CompanionBridge/ServerPlayerRuntime/ItemUsePatches.cs','CompanionBridge/ServerPlayerRuntime/MiningPatches.cs','CompanionBridge/ServerPlayerRuntime/PlacementPatches.cs','tests/PlacementPatchOfflineTests.cs',
    'experiments/virtual-player-engine-hooks/validation/ItemUseOfflineTests.cs',
    'experiments/virtual-player-engine-hooks/validation/MiningOfflineTests.cs','tests/ServerAiToolPatchTests.cs')
$references=@(Get-ChildItem -LiteralPath $refsPath -Filter '*.dll' | Select-Object -ExpandProperty FullName)+@($cecil,$utils,$detour)
# Compile against .NET 8 references rather than the PowerShell host's CLR.
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$trees=[Microsoft.CodeAnalysis.SyntaxTree[]]@($sources | ForEach-Object {[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $repo $_)))})
$metadata=[Microsoft.CodeAnalysis.MetadataReference[]]@($references | ForEach-Object {[Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_)})
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ServerAiToolChecks',$trees,$metadata,$options)
$harness=Join-Path $output 'ServerAiToolChecks.dll'
$stream=[IO.File]::Create($harness)
try{$result=$compilation.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics | ForEach-Object ToString;throw 'Tool verifier compilation failed'}
Set-Content -LiteralPath ([IO.Path]::ChangeExtension($harness,'runtimeconfig.json')) -Value '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}'
& (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') $harness $TModLoaderPath
if($LASTEXITCODE -ne 0){throw "Tool patch tests failed: $LASTEXITCODE"}
