param([string]$TModLoaderPath, [string]$EngineProfile)
# Drop identity and stance-search bounds for the ACTUAL production sources.
# Two separate compilation units: the identity fixture substitutes Terraria.Item,
# while the stance test only needs the pure geometry plus FNA's Rectangle.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine = Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath = $selectedEngine.Path
$repo = Split-Path $PSScriptRoot -Parent
$fna = Join-Path $TModLoaderPath 'Libraries/FNA/1.0.0/FNA.dll'
Add-Type -Path $fna
$refs = @(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName) + @($fna)

# 1. Drop identity lifecycle (engine substitute, production identity rules).
Add-Type -Path @(
    (Join-Path $repo 'CompanionBridge/ServerIpc/ServerDrops.cs'),
    (Join-Path $PSScriptRoot 'DropIdentityStubs.cs'),
    (Join-Path $PSScriptRoot 'DropIdentityOfflineTests.cs')
) -ReferencedAssemblies $refs
[DropIdentityOfflineTests]::Run()

# 2. Stance-search bounds (pure geometry, no world).
Add-Type -Path @(
    (Join-Path $repo 'CompanionBridge/ServerIpc/DropStanceSearch.cs'),
    (Join-Path $PSScriptRoot 'CollectStanceBoundsTests.cs')
) -ReferencedAssemblies $refs
[CollectStanceBoundsTests]::Run()
