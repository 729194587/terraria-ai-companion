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
    Write-Output "Movement engine contract $($engine.Name): $checks checks; public state getters are read-only, AnyExtraJumpUsable invokes extension hooks."
} finally {$assembly.Dispose()}
