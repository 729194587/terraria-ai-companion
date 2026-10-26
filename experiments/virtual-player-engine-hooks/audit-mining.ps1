param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output=Join-Path $repo '.local/mining-api-audit'
New-Item -ItemType Directory -Force -Path $output|Out-Null
Add-Type -Path (Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $TModLoaderPath 'tModLoader.dll'))
try {
    $names=@('Update','ItemCheck','ItemCheckWrapped','ItemCheck_Inner','ItemCheck_OwnerOnlyCode','IsTargetTileInItemRange',
        'ItemCheck_UseMiningTools','ItemCheck_UseMiningTools_ActuallyUseMiningTool','PickTile','GetPickaxeDamage',
        'DoesPickTargetTransformOnKill','ClearMiningCacheAt')
    foreach($type in $assembly.MainModule.Types|Where-Object {$_.FullName -in @('Terraria.Player','Terraria.HitTile')}) {
        foreach($method in $type.Methods|Where-Object {$_.HasBody -and ($type.Name -eq 'HitTile' -or $_.Name -in $names)}) {
            $file=Join-Path $output ($type.Name+'-'+$method.Name+'-'+$method.Parameters.Count+'.il.txt')
            $method.Body.Instructions|ForEach-Object ToString|Set-Content -LiteralPath $file -Encoding utf8
        }
    }
    $message=($assembly.MainModule.Types|Where-Object FullName -eq 'Terraria.ID.MessageID').Fields|
        Where-Object {$_.Constant -in @(13,17,20,21,41,125)}|ForEach-Object { $_.Name+'='+$_.Constant }
    $message|Set-Content -LiteralPath (Join-Path $output 'messages.txt')
    Get-FileHash -LiteralPath (Join-Path $TModLoaderPath 'tModLoader.dll') -Algorithm SHA256|
        Format-List|Out-String|Set-Content -LiteralPath (Join-Path $output 'fingerprint.txt')
} finally {$assembly.Dispose()}
Write-Output "Actual engine mining IL exported: $output"
