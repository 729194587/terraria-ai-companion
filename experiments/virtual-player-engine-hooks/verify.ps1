param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader', [switch]$NativeOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $repo '.local/virtual-player-survival-validation'
New-Item -ItemType Directory -Force $output | Out-Null
$cecil = Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
$utils = Join-Path $TModLoaderPath 'Libraries/monomod.utils/25.0.10/lib/net8.0/MonoMod.Utils.dll'
$detour = Join-Path $TModLoaderPath 'Libraries/monomod.runtimedetour/25.3.2/lib/net8.0/MonoMod.RuntimeDetour.dll'
Add-Type -Path $cecil
# tML ships the .NET 8 reference assemblies embedded in this compiler package.
# Use reference metadata rather than implementation assemblies from either CLR.
$refsPath = Join-Path $output 'net80-refs'
New-Item -ItemType Directory -Force $refsPath | Out-Null
$refPack = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $TModLoaderPath 'Libraries/basic.reference.assemblies.net80/1.5.0/lib/netstandard2.0/Basic.Reference.Assemblies.Net80.dll'))
try {
    foreach ($resource in $refPack.MainModule.Resources) {
        if ($resource.Name.StartsWith('net80.')) {
            [IO.File]::WriteAllBytes((Join-Path $refsPath ($resource.Name.Substring(6) + '.dll')), $resource.GetResourceData())
        }
    }
} finally { $refPack.Dispose() }
$references = @(Get-ChildItem $refsPath -Filter '*.dll' | Select-Object -ExpandProperty FullName) + @($cecil, $utils, $detour)
$sources = @('VirtualPlayerEngineProbe/StartupReservation.cs', 'VirtualPlayerEngineProbe/EnginePatches.cs', 'validation/EngineHookOfflineTests.cs', 'validation/NetworkSyncOfflineTests.cs', 'VirtualPlayerEngineProbe/CharacterFiles.cs', 'VirtualPlayerEngineProbe/PersistencePatches.cs', 'validation/PersistenceOfflineTests.cs', 'VirtualPlayerEngineProbe/ActorAction.cs', 'VirtualPlayerEngineProbe/ActorControl.cs', 'validation/ControlEngineStubs.cs', 'validation/ControlOfflineTests.cs', 'VirtualPlayerEngineProbe/SurvivalPatches.cs', 'validation/SurvivalOfflineTests.cs', 'validation/Program.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$harness = Join-Path $output ('EngineChecks-' + [guid]::NewGuid().ToString('N') + '.dll')
# Compile against tML's bundled .NET 8 references, not PowerShell's runtime.
# MonoMod 1.3.2 does not support this machine's PowerShell .NET 10 CLR.
# Add-Type implicitly injects the host's System.Runtime even with explicit refs.
# Use its bundled Roslyn compiler directly so the output really targets .NET 8.
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@($sources | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($_))
})
$metadata = [Microsoft.CodeAnalysis.MetadataReference[]]@($references | ForEach-Object {
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_)
})
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('EngineChecks', $trees, $metadata, $options)
$stream = [IO.File]::Create($harness)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
if (!$result.Success) { $result.Diagnostics | ForEach-Object ToString; throw 'Offline verifier compilation failed' }
Set-Content -LiteralPath ([IO.Path]::ChangeExtension($harness, 'runtimeconfig.json')) -Value '{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}'
$package = if ($NativeOnly) { '' } else { Join-Path $repo '.local/virtual-player-survival-build/Mods/VirtualPlayerEngineProbe.tmod' }
& (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') $harness $TModLoaderPath $output $package
if ($LASTEXITCODE -ne 0) { throw "Engine probe verification failed: $LASTEXITCODE" }
