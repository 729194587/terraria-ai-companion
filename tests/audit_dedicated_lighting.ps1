param([Parameter(Mandatory)][string]$TModLoaderPath)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$engine=Join-Path $TModLoaderPath 'tModLoader.dll'
$expected=[regex]::Match((Get-Content -Raw (Join-Path $repo 'CompanionBridge/ServerPlayerRuntime/EngineHooks.cs')),
    'EngineHash = "([0-9A-F]+)"').Groups[1].Value
$actual=(Get-FileHash -LiteralPath $engine).Hash
if(!$expected -or $expected -ne $actual){throw 'Expected the pinned server engine.'}
Add-Type -Path (Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $engine).Path)
try {
    "Engine SHA-256 $actual"
    $lighting=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.Lighting'
    foreach($method in $lighting.Methods | Where-Object {
        $_.Name -in @('Brightness','LightTiles') -or ($_.Name -eq 'AddLight' -and $_.Parameters.Count -eq 5)
    }) {
        $method.FullName
        $method.Body.Instructions | ForEach-Object ToString
    }
} finally {$assembly.Dispose()}
