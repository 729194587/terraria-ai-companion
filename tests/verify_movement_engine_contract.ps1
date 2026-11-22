param([string]$EngineProfile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$engine=Resolve-CompanionEngine -Name $EngineProfile
Add-Type -Path (Join-Path $engine.Path 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $engine.Path 'tModLoader.dll'))
try {
    $checks=0
    $state=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.DataStructures.ExtraJumpState'
    foreach($name in @('get_Enabled','get_Available','get_Active')){
        $method=$state.Methods | Where-Object Name -eq $name
        if(!$method -or !$method.IsPublic){throw "Missing public native state getter: $name"}
        foreach($instruction in $method.Body.Instructions){
            if($instruction.OpCode.Name -like 'stfld*' -or $instruction.OpCode.Name -like 'stsfld*'){throw "State getter mutates fields: $name"}
            if($instruction.Operand -is [Mono.Cecil.MethodReference] -and $instruction.Operand.Name -ne 'get_Enabled'){throw "Unexpected state getter call: $name -> $($instruction.Operand)"}
        }
        $checks++
    }
    $player=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.Player'
    $usable=$player.Methods | Where-Object Name -eq 'AnyExtraJumpUsable'
    $calls=@($usable.Body.Instructions | ForEach-Object Operand | Where-Object {$_ -is [Mono.Cecil.MethodReference]} | ForEach-Object Name)
    if('CanStart' -notin $calls -or 'CanStartExtraJump' -notin $calls){throw 'Recheck AnyExtraJumpUsable extension-hook assumption'}
    $checks++
    $cloud=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.ModLoader.CloudInABottleJump'
    $duration=$cloud.Methods | Where-Object Name -eq 'GetDurationMultiplier'
    if($duration.Body.Instructions.Count -ne 2 -or $duration.Body.Instructions[0].OpCode.Name -ne 'ldc.r4' -or $duration.Body.Instructions[0].Operand -ne .75){throw 'Cloud duration model changed'}
    if(@($cloud.Methods | Where-Object Name -eq 'UpdateHorizontalSpeeds').Count){throw 'Cloud now overrides horizontal physics'}
    $checks++
    $baseJump=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.ModLoader.ExtraJump'
    $horizontal=$baseJump.Methods | Where-Object Name -eq 'UpdateHorizontalSpeeds'
    if($horizontal.Body.Instructions.Count -ne 1 -or $horizontal.Body.Instructions[0].OpCode.Name -ne 'ret'){throw 'Base extra jump horizontal behavior changed'}
    $checks++
    $loader=$assembly.MainModule.Types | Where-Object FullName -eq 'Terraria.ModLoader.ExtraJumpLoader'
    $perform=$loader.Methods | Where-Object Name -eq 'PerformJump'
    $fields=@($perform.Body.Instructions | ForEach-Object Operand | Where-Object {$_ -is [Mono.Cecil.FieldReference]} | ForEach-Object Name)
    if('jumpSpeed' -notin $fields -or 'jumpHeight' -notin $fields -or 'jump' -notin $fields){throw 'Recheck launch speed/counter model'}
    $checks++
    Write-Output "Movement engine contract $($engine.Name): $checks checks; public state getters are read-only, AnyExtraJumpUsable invokes extension hooks."
} finally {$assembly.Dispose()}
