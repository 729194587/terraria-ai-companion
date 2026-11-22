param([string]$TModLoaderPath,[string]$EngineProfile,[switch]$DumpIL)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$engine=Join-Path $selectedEngine.Path 'tModLoader.dll'
Add-Type -Path (Join-Path $selectedEngine.Path 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $engine).Path)
try {
    "Engine SHA-256 $($selectedEngine.EngineHash)"
    function Method([string]$type,[string]$name,[string]$returns,[string[]]$parameters=@()) {
        $matches=@($assembly.MainModule.GetType($type).Methods | Where-Object {
            $_.Name -eq $name -and $_.ReturnType.FullName -eq $returns -and
                ($_.Parameters.ParameterType.FullName -join '|') -eq ($parameters -join '|')
        })
        if($matches.Count -ne 1 -or !$matches[0].HasBody){throw "Unsupported native lighting signature ${type}::$name"}
        Write-Host $matches[0].FullName
        if($DumpIL){$matches[0].Body.Instructions | ForEach-Object {Write-Host $_.ToString()}}
        $matches[0]
    }
    $scanner='Terraria.Graphics.Light.TileLightScanner'
    $map='Terraria.Graphics.Light.LightMap'
    $color='Microsoft.Xna.Framework.Color'
    $vector='Microsoft.Xna.Framework.Vector3'
    $void='System.Void'
    $int='System.Int32'
    $mask='Terraria.Graphics.Light.LightMaskMode'
    $null=Method $scanner '.ctor' $void
    $scan=Method $scanner GetTileLight $void @($int,$int,"$vector&")
    $null=Method $scanner GetTileMask $mask @('Terraria.Tile')
    $null=Method $map '.ctor' $void
    $null=Method $map SetSize $void @($int,$int)
    $null=Method $map SetMaskAt $void @($int,$int,$mask)
    $null=Method $map get_Item $vector @($int,$int)
    $null=Method $map set_Item $void @($int,$int,$vector)
    $blur=Method $map Blur $void
    $palette=Method 'Terraria.Main' SetBackColor $void @('Terraria.Main/InfoToSetBackColor',"$color&","$color&")
    $sky=Method 'Terraria.Main' ApplyColorOfTheSkiesToTiles $void
    $null=Method 'Terraria.Lighting' Brightness 'System.Single' @($int,$int)
    $null=Method 'Terraria.Lighting' LightTiles $void @($int,$int,$int,$int)
    $null=Method 'Terraria.Lighting' AddLight $void @($int,$int,'System.Single','System.Single','System.Single')
    $paletteWrites=@($palette.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'stsfld'} | ForEach-Object {$_.Operand.Name} | Sort-Object -Unique)
    if(($paletteWrites -join ',') -ne 'ColorOfTheSkies,eclipseLight'){throw 'Native palette writes changed.'}
    $skyTail=@($sky.Body.Instructions | Select-Object -Last 8 | ForEach-Object {$_.OpCode.Name})
    if(($skyTail -join ',') -ne 'ldsfld,ldsfld,callvirt,stsfld,ldsflda,ldsflda,call,ret'){throw 'Native sky/client tail changed.'}
    $skyCode=$sky.Body.Instructions | ForEach-Object ToString
    if((@($skyCode | Where-Object {$_ -match 'ldc.i4.7$'}).Count -ne 3) -or
        (@($skyCode | Where-Object {$_ -match 'ldc.i4.s 10$'}).Count -ne 3)){throw 'Native RGB tile sky blend changed.'}
    $scanCalls=@($scan.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.Name})
    foreach($name in 'ApplySurfaceLight','ApplyHellLight','ApplyWallLight','ApplyTileLight','ApplyLiquidLight'){
        if($name -notin $scanCalls){throw "Native scanner no longer uses $name"}
    }
    if(@($blur.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'BlurPass'}).Count -ne 2){
        throw 'Native LightMap blur passes changed.'
    }
    'Dedicated lighting signature and IL audit passed.'
} finally {$assembly.Dispose()}
