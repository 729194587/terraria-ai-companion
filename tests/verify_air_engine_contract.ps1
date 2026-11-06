param([string]$EngineProfile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$engine=Resolve-CompanionEngine -Name $EngineProfile
Add-Type -Path (Join-Path $engine.Path 'Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll')
$a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $engine.Path 'tModLoader.dll'))
try {
    $script:checks=0
    function Check([bool]$ok,[string]$why){if(!$ok){throw $why};$script:checks++}
    $player=$a.MainModule.Types | Where-Object FullName -eq 'Terraria.Player'
    $stats=(($a.MainModule.Types | Where-Object FullName -eq 'Terraria.Initializers.WingStatsInitializer').Methods | Where-Object Name -eq 'Load').Body.Instructions
    $fly=@($stats | Where-Object {$_.OpCode.Name -eq 'stloc.s' -and $_.Operand.Index -eq 11})[0]
    $speed=@($stats | Where-Object {$_.OpCode.Name -eq 'stloc.3'})[0]
    $angelRow=$false
    for($i=1;$i -lt $stats.Count-8;$i++){
        if($stats[$i-1].OpCode.Name -eq 'ldloc.0' -and $stats[$i].OpCode.Name -eq 'ldc.i4.1' -and $stats[$i+1].Operand.Index -eq 11 -and $stats[$i+2].OpCode.Name -eq 'ldloc.3' -and $stats[$i+3].Operand -eq 1 -and $stats[$i+4].OpCode.Name -eq 'ldc.i4.0' -and $stats[$i+5].Operand -eq -1 -and $stats[$i+6].Operand -eq 1){$angelRow=$true}
    }
    Check ($angelRow -and $stats[$stats.IndexOf($fly)-1].Operand -eq 100 -and $stats[$stats.IndexOf($speed)-1].Operand -eq 6.25) 'Angel registry slot 1: 100 ticks, 6.25 speed, multiplier 1, no hover'
    $wing=($player.Methods | Where-Object Name -eq 'WingMovement').Body.Instructions
    $update=($player.Methods | Where-Object Name -eq 'Update').Body.Instructions
    $air=($player.Methods | Where-Object Name -eq 'WingAirLogicTweaks').Body.Instructions
    $glide=@($update | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'WingGlidingSpeeds'})
    Check ($glide.Count -eq 1) 'native single glide hook boundary'
    $i=$update.IndexOf($glide[0]);$before=@($update[[Math]::Max(0,$i-8)..($i-1)] | Where-Object {$_.OpCode.Name -eq 'ldc.r4'} | ForEach-Object Operand)
    Check ($before.Count -eq 2 -and [Math]::Abs($before[0]-1.0/3) -lt .00001 -and [Math]::Abs($before[1]-1.0/3) -lt .00001) 'native ordinary glide gravity/fall multipliers'
    $v=@($wing | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'VerticalWingSpeeds'})
    Check ($v.Count -eq 1) 'native single vertical wing hook boundary'
    $defaults=$false
    for($i=0;$i -lt $wing.Count-9;$i++){
        if($wing[$i].OpCode.Name -eq 'ldc.r4' -and [Math]::Abs([double]$wing[$i].Operand-.1) -lt .000001 -and $wing[$i+1].OpCode.Name -eq 'stloc.0'){
            $values=@(0,2,4,6,8 | ForEach-Object {$wing[$i+$_].Operand})
            $expected=@(.1,.5,1.5,.5,.1);$matches=$true
            for($j=0;$j -lt 5;$j++){if([Math]::Abs([double]$values[$j]-$expected[$j]) -gt .000001){$matches=$false}}
            if($matches){$defaults=$true}
        }
    }
    Check $defaults 'Angel default ascent coefficients .1/.5/1.5/.5/.1'
    $angelOverride=$false
    for($i=0;$i -lt $wing.Count-1;$i++){
        if($wing[$i].Operand -is [Mono.Cecil.FieldReference] -and $wing[$i].Operand.Name -eq 'wingsLogic' -and $wing[$i+1].OpCode.Name -eq 'ldc.i4.1'){$angelOverride=$true}
    }
    Check (!$angelOverride) 'Angel slot has no special wing ascent branch'
    $fuel=@($wing | Where-Object {$_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'wingTime'})
    Check (@($fuel | Where-Object {$wing[$wing.IndexOf($_)-2].OpCode.Name -eq 'ldc.r4' -and $wing[$wing.IndexOf($_)-2].Operand -eq 1}).Count -ge 1) 'normal wings consume one wingTime per powered update'
    Check (@($air | Where-Object {$_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'AccRunSpeedOverride'}).Count -gt 0) 'air horizontal parameters come from native wing stats before sampling'
    $gate=$update | Where-Object {$_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'rocketDelay'}
    $pulse=@($gate | Where-Object {$update[$update.IndexOf($_)-1].Operand -eq 10})
    Check ($pulse.Count -eq 1) 'rocket trigger creates ten-update pulse'
    $i=$update.IndexOf($pulse[0]);$near=$update[($i-75)..$i]
    foreach($name in @('rocketDelay','canRocket','rocketRelease','rocketTime')){
        Check (@($near | Where-Object {$_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $name}).Count -gt 0) "rocket eligibility includes $name"
    }
    Check (@($near | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'AnyExtraJumpUsable'}).Count -eq 1) 'native rocket arbitration checks extra jumps; model must not call it'
    $body=($update | ForEach-Object ToString) -join "`n"
    Check ($body.Contains('ldc.i4.6') -and $body.Contains('wingTimeMax')) 'native wing/fuel conversion block exists'
    $jump=($player.Methods | Where-Object Name -eq 'JumpMovement').Body.Instructions
    Check (@($jump | Where-Object {$_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'rocketRelease'}).Count -eq 2) 'rocket release latch follows native jump/release'
    foreach($field in @('rocketFrame','rocketDelay','rocketDelay2','canRocket','rocketRelease')){
        Check (@($player.Fields | Where-Object Name -eq $field).Count -eq 1) "native field $field available"
    }
    Write-Output "Air engine contract $($engine.Name): $script:checks checks passed; IL inspection only, no Player.Update or world run."
} finally {$a.Dispose()}
