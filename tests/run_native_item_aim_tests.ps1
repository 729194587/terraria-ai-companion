param([string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cecil = Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
$fna = Join-Path $TModLoaderPath 'Libraries/FNA/1.0.0/FNA.dll'
Add-Type -Path $cecil
Add-Type -Path $fna
$references = @(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName) + @($cecil,$fna)
Add-Type -Path @((Join-Path $PSScriptRoot 'NativeItemAimTests.cs'), (Join-Path $repo 'CompanionBridge/ItemAim.cs')) -ReferencedAssemblies $references -CompilerOptions "/nowarn:1701"
[NativeItemAimTests]::Run($TModLoaderPath)
