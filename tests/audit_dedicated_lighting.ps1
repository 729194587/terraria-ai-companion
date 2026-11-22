param([string]$TModLoaderPath,[string]$EngineProfile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
$repo=Split-Path $PSScriptRoot -Parent
$engine=Join-Path $TModLoaderPath 'tModLoader.dll'

Add-Type -Path (Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $engine).Path)
try {
    "Engine SHA-256 $selectedEngine.EngineHash"
    $lighting=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.Lighting'
    foreach($method in $lighting.Methods | Where-Object {
        $_.Name -in @('Brightness','LightTiles') -or ($_.Name -eq 'AddLight' -and $_.Parameters.Count -eq 5)
    }) {
        $method.FullName
        $method.Body.Instructions | ForEach-Object ToString
    }
} finally {$assembly.Dispose()}
