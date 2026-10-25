param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader')
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output=Join-Path $repo '.local/item-use-api-audit'
New-Item -ItemType Directory -Force -Path $output|Out-Null
Add-Type -Path (Join-Path $TModLoaderPath 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $TModLoaderPath 'tModLoader.dll'))
try {
    $player=$assembly.MainModule.Types|Where-Object FullName -eq 'Terraria.Player'
    $names=@('Update','ItemCheck','ItemCheckWrapped','ItemCheck_Inner','ItemCheck_OwnerOnlyCode',
        'ItemCheck_CheckCanUse','ItemCheck_StartActualUse','ItemCheck_UseMiningTools','ItemCheck_UseMiningTools_ActuallyUseMiningTool',
        'ApplyItemAnimation','ApplyItemTime','SetItemAnimation','SetItemTime','TryAllowingItemReuse','ApplyReuseDelay')
    foreach($method in $player.Methods|Where-Object {$_.Name -in $names -and $_.HasBody}) {
        $file=Join-Path $output ('Player-'+$method.Name+'-'+$method.Parameters.Count+'.il.txt')
        $method.Body.Instructions|ForEach-Object ToString|Set-Content -LiteralPath $file -Encoding utf8
    }
    foreach($entry in @(@('Terraria.NetMessage','SendData',0x20db,0x2110),@('Terraria.MessageBuffer','GetData',0x4f89,0x501d))) {
        $method=($assembly.MainModule.Types|Where-Object FullName -eq $entry[0]).Methods|Where-Object Name -eq $entry[1]
        $method.Body.Instructions|Where-Object {$_.Offset -ge $entry[2] -and $_.Offset -lt $entry[3]}|
            ForEach-Object ToString|Set-Content -LiteralPath (Join-Path $output ($entry[0]+'-'+$entry[1]+'-41.il.txt')) -Encoding utf8
    }
    Get-FileHash -LiteralPath (Join-Path $TModLoaderPath 'tModLoader.dll') -Algorithm SHA256|Select-Object Hash
    Write-Output "Read-only native IL audit: $output"
} finally {$assembly.Dispose()}
