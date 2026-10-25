param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $repo '.local/virtual-player-validation'
New-Item -ItemType Directory -Force $output | Out-Null
$cecil = Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
Add-Type -Path $cecil
$references = @(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName) + @($cecil)
Add-Type -Path (Join-Path $PSScriptRoot 'validation/NativeLifecycleTests.cs') -ReferencedAssemblies $references -CompilerOptions '/nowarn:1701'
$package = Join-Path $repo '.local/virtual-player-build/Mods/VirtualPlayerProbe.tmod'
if (!(Test-Path $package)) { throw 'Build the isolated probe with build.ps1 before verification.' }
[NativeLifecycleTests]::Run($TModLoaderPath, $output, (Join-Path $PSScriptRoot 'VirtualPlayerProbe/VirtualPlayerProbe.cs'), $package)
