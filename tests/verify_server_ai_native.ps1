param([string]$TModLoaderPath,[string]$EngineProfile,[string]$WorldPath,[int]$Port=27841,[int]$IpcPort=27842,[string]$PackagePath,[switch]$Atomic,[switch]$Reads,[switch]$Actions,[switch]$Agent,[switch]$LiveModel,[switch]$Perception,[switch]$Exploration,[string]$PythonExecutable,[switch]$Landing,[switch]$Semantic,[switch]$Terrain,[switch]$LandingRegions,[switch]$Placement,[switch]$Scene,[switch]$Crafting,[switch]$Inventory,[switch]$Tasks,[switch]$AutonomousNavigation,[switch]$NavigationSafety,[string[]]$NavigationSafetyCases,[switch]$VerticalNavigation,[switch]$BuildDiff,[switch]$BuildPlace,[switch]$Housing,[switch]$NpcHome,[switch]$G3Shop,[switch]$MerchantLua,[switch]$MerchantJourney,[ValidateSet('Merchant','Nurse')][string]$TalkToNpc,[switch]$NurseLua,[switch]$ReforgeLua,[switch]$TcpCapacity,[switch]$Mobility,[string]$BenchmarkSpec,[string]$BenchmarkProfile,[switch]$KeepProfile,[switch]$ItemInfo,[string[]]$ModPackages,[switch]$CompatibilityQueries)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
if([bool]$BenchmarkSpec -ne [bool]$BenchmarkProfile){throw 'Benchmark requires a spec and a fresh profile path.'}
if($BenchmarkSpec){
    if(Test-Path -LiteralPath $BenchmarkProfile){throw 'Benchmark profile must be new.'}
    $BenchmarkSpec=(Resolve-Path -LiteralPath $BenchmarkSpec).Path
    $Actions=$true
}
if(($Tasks -or $AutonomousNavigation -or $NavigationSafety) -and !$Actions){throw 'Task validation requires isolated Actions fixture.'}
if($Inventory -and !$Actions){throw 'Inventory validation requires isolated Actions fixture.'}
if($Crafting -and !$Actions){throw 'Crafting verification requires isolated Actions fixture.'}
if($Scene){$Perception=$true;if(!$Actions){throw 'Scene verification requires isolated Actions fixture.'}}
if($LandingRegions -or $Mobility){$Terrain=$true}
if($VerticalNavigation){$Terrain=$true}
if($Placement -and !$Actions){throw 'Placement validation requires isolated Actions fixture.'}
if($Terrain -and !$Actions){throw 'Terrain validation requires isolated Actions fixture.'}
if($Semantic -and (!$Actions -or !$Perception)){throw 'Semantic validation requires isolated Actions and Perception fixtures.'}
if($Landing -and !$Actions){throw 'Landing cleanup validation requires the isolated Actions fixture.'}
if($Port -eq $IpcPort){throw 'Native game and AI IPC test ports must be distinct.'}
$occupied=@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {$_.Port -in @($Port,$IpcPort)})
if($occupied.Count){throw 'Native test port already occupied; refusing to connect to an unrelated process.'}
. (Join-Path $PSScriptRoot 'server_ai_native_evidence.ps1')
$repo=Split-Path $PSScriptRoot -Parent
if(!$WorldPath){$WorldPath=Join-Path $repo '.local/virtual-player-control-host/Worlds/test.wld'}
$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path
$package=if($PackagePath){(Resolve-Path -LiteralPath $PackagePath).Path}else{$selectedEngine.Package}
Assert-CompanionPackage -Package $package -Engine $selectedEngine
if($TalkToNpc){$MerchantJourney=$true;$NurseLua=$TalkToNpc -eq 'Nurse'}
if($MerchantJourney){$MerchantLua=$true;$Terrain=$true}
if($ReforgeLua -or $NurseLua){$MerchantLua=$true}
if(!$PythonExecutable){$PythonExecutable=if($Atomic -or $NpcHome -or $MerchantLua -or $ItemInfo){Join-Path $repo '.local/lua-venv/Scripts/python.exe'}else{'python'}}
if(!(Test-Path -LiteralPath $package)){throw 'Build CompanionBridge first.'}
$profile=if($BenchmarkSpec){[IO.Path]::GetFullPath($BenchmarkProfile)}else{Join-Path $repo ('.local/companion-'+$(if($Atomic){'g82'}else{'g81'})+'-native-'+[guid]::NewGuid().ToString('N'))}
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
$oldLogs=Join-Path $TModLoaderPath 'tModLoader-Logs'
if(!$BenchmarkSpec -and (Test-Path -LiteralPath $oldLogs)){Copy-Item -LiteralPath $oldLogs -Destination "$profile/previous-installation-logs" -Recurse}
Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
$modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
. (Join-Path $PSScriptRoot 'native_mod_packages.ps1')
$enabled = @(Set-NativeModPackages $profile $ModPackages)
if($MerchantLua){$G3Shop=$true}
if($NpcHome){$Housing=$true}
if($BuildPlace -or $Housing -or $G3Shop){$Actions=$true}
if($Actions){[IO.File]::WriteAllText("$profile/isolated-action-fixture",'Explicit test fixture in this copied world only.')}
if($BenchmarkSpec){[IO.File]::WriteAllText("$profile/isolated-benchmark-fixture",'One benchmark attempt in a copied world.')}
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
foreach($argument in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionengineprofile',$selectedEngine.Name,'-companionaiport',"$IpcPort",'-companionaiipctest')){$info.ArgumentList.Add($argument)}
if($TcpCapacity){$info.ArgumentList.Add('-maxplayers');$info.ArgumentList.Add('2')}
if($Tasks -or $AutonomousNavigation -or $NavigationSafety){$info.ArgumentList.Add("-companiontasktest")}
if($Inventory){$info.ArgumentList.Add("-companioninventorytest")}
if($Crafting){$info.ArgumentList.Add("-companioncrafttest")}
if($Scene){$info.ArgumentList.Add("-companionscenetest")}
if($ReforgeLua){$info.ArgumentList.Add('-companionreforgetest')}
if($TalkToNpc){$info.ArgumentList.Add('-companiontalkjourney')}
if($NurseLua){$info.ArgumentList.Add('-companionnursetest')}
if($MerchantJourney){$info.ArgumentList.Add('-companionmerchantjourney');$info.ArgumentList.Add('-companionnavrecoverytest')}
if($MerchantLua){$info.ArgumentList.Add('-companionmerchantluatest')}
if($G3Shop){$info.ArgumentList.Add('-companiong3shoptest')}
if($NpcHome){$info.ArgumentList.Add('-companionnpchometest')}
if($Housing){$info.ArgumentList.Add('-companionhousingtest')}
if($Placement -or $BuildPlace){$info.ArgumentList.Add("-companionplacementtest")}
if($Perception){$info.ArgumentList.Add("-companionperceptiontest")}
if($Semantic){$info.ArgumentList.Add("-companionsemantictest")}
if($Terrain){$info.ArgumentList.Add("-companionterraintest")}
if($Mobility){$info.ArgumentList.Add("-companionmobilitytest")}
if($Actions){$info.ArgumentList.Add('-companionactionstest')}
if($BenchmarkSpec){$info.ArgumentList.Add('-companionbenchmarktest')}
if($Landing){$info.ArgumentList.Add('-companionlandingtest')}
$process=[Diagnostics.Process]::Start($info)
$startedUtc=$process.StartTime.ToUniversalTime()
$stdout=$process.StandardOutput.ReadToEndAsync()
$stderr=$process.StandardError.ReadToEndAsync()
$client=$null
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    while([DateTime]::UtcNow -lt $deadline -and !$process.HasExited){
        try {$client=[Net.Sockets.TcpClient]::new('127.0.0.1',$IpcPort);break}catch {Start-Sleep -Milliseconds 200}
    }
    if(!$client){throw 'Native IPC listener did not start.'}
    $stream=$client.GetStream();$stream.ReadTimeout=6000
    $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$false,1024,$true)
    function Request($value){
        $bytes=[Text.Encoding]::UTF8.GetBytes(($value|ConvertTo-Json -Compress -Depth 8)+"`n")
        $stream.Write($bytes,0,$bytes.Length)
        $text=$reader.ReadLine()
        if(!$text){throw 'IPC closed without response'}
        $text | ConvertFrom-Json
    }
    $bound=$null
    while([DateTime]::UtcNow -lt $deadline){
        $bound=Request @{v=1;id='native-bind';op='bind'}
        if($bound.ok){break}
        if($bound.error -ne 'identity_unavailable'){throw ($bound|ConvertTo-Json -Compress)}
        Start-Sleep -Milliseconds 100
    }
    if(!$bound.ok){throw 'Native actor never became available'}
    if ($ItemInfo) {
        $bound.data | ConvertTo-Json -Depth 8 | Set-Content "$profile/handshake.json"
        foreach ($mod in $enabled) {
            if ($mod -notin $bound.data.compatibility.loadedMods.name) { throw "Server did not load $mod" }
        }
        $options = @()
        if ('CalamityMod' -in $enabled) { $options += '--calamity' }
        & $PythonExecutable (Join-Path $repo 'tests/native_item_info.py') --port $IpcPort --profile $profile @options
        if ($LASTEXITCODE -ne 0) { throw 'Native item info verification failed' }
        Write-Output "Item info results: $profile/item-info.json"
        if ($CompatibilityQueries) {
            & $PythonExecutable (Join-Path $repo 'tests/native_mod_queries.py') --port $IpcPort --profile $profile
            if ($LASTEXITCODE -ne 0) { throw "Registry/scene compatibility check failed: $profile" }
        } elseif (!$Atomic -and !$TcpCapacity) { return }
    }
    $state=Request @{v=1;id='native-state';op='state';binding=$bound.binding}
    if(!$state.ok -or !$state.data.active -or $state.data.inventory.Count -lt 50 -or $bound.binding.generation -lt 1){throw 'Native state contract failed'}
    $mismatchedBinding=@{actor=$bound.binding.actor;world=$bound.binding.world;generation=([long]$bound.binding.generation+1);slot=$bound.binding.slot}
    $bindingMismatch=Request @{v=1;id='native-binding-mismatch';op='state';binding=$mismatchedBinding}
    if($bindingMismatch.ok -or $bindingMismatch.error -ne 'stale_binding'){throw 'Mismatched request binding was not rejected'}
    if($BuildPlace -or $Housing -or $G3Shop){
        if(!$G3Shop -and 'server_ai_build_place_v1' -notin $bound.data.capabilities){throw 'Missing build_place capability'}
        $probe=if($TalkToNpc){'tests/native_talk_to.py'}elseif($ReforgeLua){'tests/native_reforge_lua.py'}elseif($NurseLua){'tests/native_nurse_lua.py'}elseif($MerchantJourney){'tests/native_merchant_journey.py'}elseif($MerchantLua){'tests/native_merchant_lua.py'}elseif($G3Shop){'tests/native_g3_shop.py'}elseif($Housing){'tests/native_housing.py'}else{'tests/native_build_place.py'}
        [string[]]$probeArgs=if($TalkToNpc){@('--npc',$TalkToNpc)}elseif($NpcHome){@('--npc-home')}else{@()}
        & $PythonExecutable (Join-Path $repo $probe) --port $IpcPort --profile $profile @probeArgs
        if($LASTEXITCODE -ne 0){throw "Native verification failed in $profile"}
        $client.Dispose();$client=$null
        [IO.File]::WriteAllText("$profile/g81-test-stop",'Isolated test complete.')
        if(!$process.WaitForExit(30000)){throw 'Diagnostic server did not exit'}
        if($process.ExitCode -ne 0){throw "Diagnostic server exited $($process.ExitCode)"}
        return
    }
    if($BuildDiff){
        if('server_ai_build_diff_v1' -notin $bound.data.capabilities){throw 'Missing build_diff capability'}
        $origin=@{x=[int][math]::Floor($state.data.position.x/16); y=[int][math]::Floor($state.data.position.y/16)}
        $tiles=@(foreach($y in 0..7){foreach($x in 0..7){
            if($x -in 0,7 -or $y -in 0,7){@{x=$x;y=$y;itemType=9}}
        }})
        $walls=@(foreach($y in 1..6){foreach($x in 1..6){@{x=$x;y=$y;itemType=93}}})
        $blueprint=@{origin=$origin;tiles=$tiles;walls=$walls;objects=@(@{x=3;y=6;itemType=36})}
        $query=@{v=1;id='build-diff';op='query';operation='build_diff';binding=$bound.binding;arguments=@{blueprint=$blueprint}}
        $comparison=Request $query
        if(!$comparison.ok){throw ($comparison|ConvertTo-Json -Depth 12)}
        $diff=$comparison.data.data
        if($diff.entries.Count -ne 65 -or $diff.matched+$diff.pending -ne 65){throw 'Incomplete blueprint comparison'}
        $bench=@($diff.entries|Where-Object layer -eq 'objects')
        if($bench.Count -ne 1 -or $bench[0].width -ne 2 -or $bench[0].height -ne 1){throw 'Work bench native footprint mismatch'}
        $cost=@($diff.materials|Where-Object itemType -eq 36)
        if($bench[0].status -ne 'already_matches' -and ($cost.Count -ne 1 -or $cost[0].required -ne 1)){throw 'Work bench must cost one item'}
        $comparison|ConvertTo-Json -Depth 16|Set-Content -LiteralPath "$profile/build-diff.json"
        & $PythonExecutable (Join-Path $repo 'tests/native_build_diff.py') --port $IpcPort --profile $profile
        if($LASTEXITCODE -ne 0){throw 'Lua build_diff native verification failed'}
        $query.arguments.blueprint.tiles+=@($tiles[0])
        $duplicate=Request $query
        if($duplicate.ok -or $duplicate.error -ne 'invalid_request'){throw 'Duplicate blueprint target accepted or misclassified'}
    }
    $action=Request @{v=1;id='native-mine';op='mine'}
    if($action.ok -or $action.error -ne 'unsupported_operation'){throw 'Action isolation failed'}
    $pythonText=& $PythonExecutable (Join-Path $repo 'scripts/ai_player_state.py') --port $IpcPort
    if($LASTEXITCODE -ne 0){throw 'Python read-only client failed'}
    $pythonState=($pythonText -join "`n")|ConvertFrom-Json
    if(!$pythonState.ok -or $pythonState.binding.actor -ne $bound.binding.actor){throw 'Python client returned a different actor identity'}
    $atomicResult=$null
    if($Atomic){
        $null=& $PythonExecutable (Join-Path $repo 'scripts/server_ai_select.py') --port $IpcPort --key 0 --output "$profile/lua-select-result.json"
        if($LASTEXITCODE -ne 0){throw 'Existing Lua worker did not complete native AI selection'}
        $atomicResult=Get-Content -LiteralPath "$profile/lua-select-result.json" -Raw|ConvertFrom-Json
        $after=Request @{v=1;id='native-after-selection';op='state';binding=$bound.binding}
        if(!$atomicResult.lastAction.completed -or $atomicResult.lastAction.endEvidence.selectedSlot -ne 9 `
            -or !$atomicResult.lastAction.endEvidence.selectionVerified -or !$after.ok -or $after.data.selectedSlot -ne 9){throw 'Native selection completion evidence disagrees with actual Player selected slot'}
    }
    $readResult=$null
    $combinedResult=$null
    if($Reads){
        $null=& $PythonExecutable (Join-Path $repo 'scripts/server_ai_verify.py') --port $IpcPort --key 2 --output "$profile/lua-combined-result.json"
        if($LASTEXITCODE -ne 0){throw 'Combined Lua player/inventory/selection verification failed'}
        $combinedResult=Get-Content -LiteralPath "$profile/lua-combined-result.json" -Raw|ConvertFrom-Json
        if($combinedResult.actions -ne 1 -or $combinedResult.value.before.selectedSlot -ne 9 -or $combinedResult.value.after.selectedSlot -ne 1 `
            -or !$combinedResult.value.selection.completed){throw 'Combined Lua sequence did not prove selection from key 0 to key 2'}
        $null=& $PythonExecutable (Join-Path $repo 'scripts/server_ai_verify.py') --port $IpcPort --output "$profile/lua-read-result.json"
        if($LASTEXITCODE -ne 0){throw 'Existing Lua worker did not complete native player/inventory reads'}
        $readResult=Get-Content -LiteralPath "$profile/lua-read-result.json" -Raw|ConvertFrom-Json
        $rawQuery=Request @{v=1;id='native-player-query';op='query';binding=$bound.binding;operation='get_player_state';arguments=@{}}
        $rawState=Request @{v=1;id='native-state-after-reads';op='state';binding=$bound.binding}
        if(!$rawQuery.ok -or !$rawState.ok -or $readResult.actions -ne 0 -or $readResult.value.fullInventorySlots -ne 59 `
            -or $rawQuery.data.data.player.life -ne $rawState.data.life -or $rawQuery.data.data.player.position.x -ne $rawState.data.position.x `
            -or $rawQuery.data.data.player.position.y -ne $rawState.data.position.y `
            -or $readResult.value.after.selectedSlot -ne $rawState.data.selectedSlot){throw 'Lua/native query evidence disagrees with the raw bound Player state'}
        $readMismatch=Request @{v=1;id='native-read-binding-mismatch';op='query';binding=$mismatchedBinding;operation='inspect_inventory';arguments=@{}}
        if($readMismatch.ok -or $readMismatch.error -ne 'stale_binding'){throw 'Mismatched read binding was not rejected'}
    }
    $actionResult=$null;$worldEvidence=$null
    if($BenchmarkSpec){
        [IO.File]::WriteAllText("$profile/benchmark-binding.json",($bound.binding|ConvertTo-Json -Compress))
        & $PythonExecutable (Join-Path $repo 'tests/benchmark/runner.py') attempt --port $IpcPort --profile $profile --spec $BenchmarkSpec
        if($LASTEXITCODE -ne 0){throw 'Benchmark attempt process failed to write evidence.'}
    }
    if($Actions -and !$BenchmarkSpec){
        $fixturePath=Join-Path $profile 'action-fixture.json'
        $fixtureDeadline=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath $fixturePath) -and [DateTime]::UtcNow -lt $fixtureDeadline){Start-Sleep -Milliseconds 100}
        $fixture=Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
        if($Perception){
            & $PythonExecutable (Join-Path $repo 'scripts/server_ai_perception.py') --port $IpcPort --output "$profile/lua-perception-result.json"
            if($LASTEXITCODE -ne 0){throw 'Native Lua perception verification failed'}
            $perceptionResult=Get-Content -LiteralPath "$profile/lua-perception-result.json" -Raw|ConvertFrom-Json
            if(!@($perceptionResult.value.npcs.data.items | Where-Object slot -eq $fixture.perceptionNpcSlot).Count){throw 'Perception did not observe the controlled fixture NPC'}
        }
        if($Scene){
            & $PythonExecutable (Join-Path $repo 'tests/native_scene.py') --port $IpcPort --profile $profile --phase perception
            if($LASTEXITCODE -ne 0){throw "Scene perception verification failed: $profile/scene-perception.json"}
        }
        # The live NPC used by perception must not leak contact damage/knockback
        # into the following non-combat action checks (including the semantic chain).
        [IO.File]::WriteAllText("$profile/action-fixture-no-combat",'End NPC perception phase; isolate subsequent action checks.')
        $isolationDeadline=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath "$profile/action-fixture-no-combat.json") -and [DateTime]::UtcNow -lt $isolationDeadline){Start-Sleep -Milliseconds 100}
        $combatIsolation=Get-Content -LiteralPath "$profile/action-fixture-no-combat.json" -Raw|ConvertFrom-Json
        if($combatIsolation.world -ne $bound.binding.world -or $combatIsolation.generation -ne $bound.binding.generation -or $combatIsolation.slot -ne $bound.binding.slot){throw 'Combat isolation acknowledgement has stale binding'}
        if($Semantic){
            & $PythonExecutable (Join-Path $repo 'scripts/server_ai_semantic.py') --port $IpcPort --output "$profile/lua-semantic-result.json"
            if($LASTEXITCODE -ne 0){throw 'Native semantic verification failed'}
            $semanticResult=Get-Content -LiteralPath "$profile/lua-semantic-result.json" -Raw|ConvertFrom-Json
        }
        if($Agent){
            $agentArgs=@((Join-Path $repo 'tests/native_server_ai_agent.py'),'--port',"$IpcPort",'--output',"$profile/agent-native-result.json")
            if($LiveModel){$agentArgs+='--live-model'}
            if($Exploration){$agentArgs+='--exploration'}
            & $PythonExecutable @agentArgs
            if($LASTEXITCODE -ne 0){throw "Existing Agent/model/Lua/native AI validation failed: $profile/agent-native-result.json"}
            $agentResult=Get-Content -LiteralPath "$profile/agent-native-result.json" -Raw|ConvertFrom-Json
        }
        function Start-NativeAction($operation,$arguments){
            $id=[guid]::NewGuid().ToString('N')
            $reply=Request @{v=1;id='native-action-start';op='start';binding=$bound.binding;actionId=$id;operation=$operation;arguments=$arguments;expiresAtUnixMs=([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+1500)}
            if(!$reply.ok){throw ($reply|ConvertTo-Json -Compress -Depth 8)}
            $id
        }
        function Cancel-RunningNativeAction($operation,$arguments){
            $id=Start-NativeAction $operation $arguments
            $until=[DateTime]::UtcNow.AddSeconds(3)
            do {
                Start-Sleep -Milliseconds 20
                $poll=Request @{v=1;id='native-action-poll';op='action';binding=$bound.binding;actionId=$id}
                if(!$poll.ok -or $poll.data.action.status -ne 'running'){throw "Native $operation ended before cancellation test"}
                $e=$poll.data.action.evidence
            } while((!$e -or !($e.updates -gt 0 -or $e.Ticks -gt 0)) -and [DateTime]::UtcNow -lt $until)
            if(!$e -or !($e.updates -gt 0 -or $e.Ticks -gt 0)){throw 'Native action never entered actual player updates'}
            $cancel=Request @{v=1;id='native-action-cancel';op='cancel';binding=$bound.binding;actionId=$id}
            if(!$cancel.ok -or $cancel.data.action.status -ne 'cancelled' -or !$cancel.data.action.endEvidence.inputsReleased){throw 'Native cancellation has no confirmed input release'}
            $cancel.data.action
        }
        if($Scene){
            & $PythonExecutable (Join-Path $repo 'tests/native_scene.py') --port $IpcPort --profile $profile --phase actions
            if($LASTEXITCODE -ne 0){throw "Scene action verification failed: $profile/scene-actions.json"}
        }
        if($Inventory){
            & $PythonExecutable (Join-Path $repo 'tests/native_inventory.py') --port $IpcPort --profile $profile
            if($LASTEXITCODE -ne 0){throw "Inventory verification failed: $profile/inventory-results.json"}
        }
        if($Crafting){
            & $PythonExecutable (Join-Path $repo 'tests/native_crafting.py') --port $IpcPort --profile $profile
            if($LASTEXITCODE -ne 0){throw "Crafting verification failed: $profile/crafting-results.json"}
        }
        if(!$Scene -and !$Crafting){
        $nativeCancellations=@{}
        $nativeCancellations.navigation=Cancel-RunningNativeAction 'navigate_to_x' @{targetX=($fixture.targetX+32)}
        if($LandingRegions){
            $regionState=Request @{v=1;id='region-preflight-state';op='query';binding=$bound.binding;operation='get_player_state';arguments=@{}}
            $regionFeet=$regionState.data.data.player.position.y+42
            $regionInvalid=Request @{v=1;id='region-invalid';op='start';binding=$bound.binding;actionId=([guid]::NewGuid().ToString('N'));operation='navigate_to_landing_region';arguments=@{minPlayerLeftX=20;maxPlayerLeftX=10;surfaceY=$regionFeet};expiresAtUnixMs=([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+1500)}
            if($regionInvalid.ok -or $regionInvalid.error -ne 'invalid_arguments'){throw 'Native landing preflight accepted reversed bounds'}
            $nativeCancellations.landingRegion=Cancel-RunningNativeAction 'navigate_to_landing_region' @{minPlayerLeftX=($fixture.targetX+32.003);maxPlayerLeftX=($fixture.targetX+64.007);surfaceY=$regionFeet}
            if(!$nativeCancellations.landingRegion.targetRegion -or $nativeCancellations.landingRegion.evidence.arrived){throw 'Cancelled region lost its target or falsely arrived'}
        }
        $null=& $PythonExecutable (Join-Path $repo 'scripts/server_ai_actions.py') --port $IpcPort --target-x $fixture.targetX --use-key 1 --consume-key 3 --pick-key 2 --mine-x $fixture.mineX --mine-y $fixture.mineY --output "$profile/lua-actions-result.json"
        if($LASTEXITCODE -ne 0){throw "Lua native actions failed: $profile/lua-actions-result.json"}
        $actionResult=Get-Content -LiteralPath "$profile/lua-actions-result.json" -Raw|ConvertFrom-Json
        [IO.File]::WriteAllText("$profile/g83-test-snapshot",'Capture actual bound actor, tiles, drops after Lua actions.')
        $snapshotDeadline=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath "$profile/action-world-evidence.json") -and [DateTime]::UtcNow -lt $snapshotDeadline){Start-Sleep -Milliseconds 100}
        $worldEvidence=Get-Content -LiteralPath "$profile/action-world-evidence.json" -Raw|ConvertFrom-Json
        if($actionResult.actions -ne 7 -or $actionResult.actionState -ne 'released' -or !$worldEvidence.targetAbsent -or !$worldEvidence.neighborPresent `
            -or !$worldEvidence.controlsReleased -or $worldEvidence.appleStack -ne 1 `
            -or !$worldEvidence.observedDrop.items.Count -or $worldEvidence.gameUpdate -le 0 -or $worldEvidence.networkControls -le 0){throw 'Lua receipts disagree with actual native movement/use/consumption/mining/drop/release observations'}
        $nativeCancellations.use=Cancel-RunningNativeAction 'use_item' @{}
        if($nativeCancellations.use.evidence.cycleVerified){throw 'Cancelled animation must not claim a naturally completed native cycle'}
        $nativeCancellations.mining=Cancel-RunningNativeAction 'mine_tile' @{x=$fixture.mineX;y=($fixture.mineY-1)}
        # Re-observe the real world after cancelling partially progressed mining.
        Remove-Item -LiteralPath "$profile/action-world-evidence.json"
        [IO.File]::WriteAllText("$profile/g83-test-snapshot",'Capture cancellation evidence.')
        $snapshotDeadline=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath "$profile/action-world-evidence.json") -and [DateTime]::UtcNow -lt $snapshotDeadline){Start-Sleep -Milliseconds 100}
        $worldAfterCancel=Get-Content -LiteralPath "$profile/action-world-evidence.json" -Raw|ConvertFrom-Json
        if(!$worldAfterCancel.controlsReleased -or !$worldAfterCancel.neighborPresent -or !$worldAfterCancel.cancellationTargetPresent){throw 'Native cancel left held inputs or destroyed the sentinel/cancellation target'}
        $emptyId=Start-NativeAction 'mine_tile' @{x=$fixture.mineX;y=$fixture.mineY}
        $until=[DateTime]::UtcNow.AddSeconds(3)
        do {
            Start-Sleep -Milliseconds 20
            $empty=Request @{v=1;id='native-empty-target';op='action';binding=$bound.binding;actionId=$emptyId}
        } while($empty.ok -and $empty.data.action.status -eq 'running' -and [DateTime]::UtcNow -lt $until)
        if(!$empty.ok -or $empty.data.action.status -ne 'failed' -or !$empty.data.action.endEvidence.inputsReleased){throw 'Empty tile was not a confirmed native refusal'}
        }
    }
    if($Placement){
        & $PythonExecutable (Join-Path $repo 'tests/native_server_ai_placement.py') --port $IpcPort --profile $profile
        if($LASTEXITCODE -ne 0){throw "Native placement Lua verification failed: $profile/placement-lua-result.json"}
    }
    if($Terrain){
        $terrainScripts=@()
        if($Mobility){$terrainScripts+='tests/native_mobility.py'}
        if($LandingRegions){$terrainScripts+='tests/native_landing_regions.py'}
        if($VerticalNavigation){$terrainScripts+='tests/native_vertical_navigation.py'}
        if(!$terrainScripts.Count){$terrainScripts+='scripts/server_ai_terrain.py'}
        foreach($terrainScript in $terrainScripts){
            & $PythonExecutable (Join-Path $repo $terrainScript) --port $IpcPort --profile $profile
            if($LASTEXITCODE -ne 0){throw "Native terrain matrix failed: $profile"}
        }
    }
    if($NavigationSafety){
        $safetyArguments=@('--port',"$IpcPort",'--profile',$profile)
        foreach($case in $NavigationSafetyCases){$safetyArguments+=@('--case',$case)}
        & $PythonExecutable (Join-Path $repo 'tests/native_navigation_safety.py') @safetyArguments
        if($LASTEXITCODE -ne 0){throw "Native navigation safety verification failed: $profile/navigation-safety-results.json"}
    }

    if($Tasks -or $AutonomousNavigation){
        $taskArguments=@('--port',$IpcPort,'--profile',$profile)
        if($AutonomousNavigation -and !$Tasks){$taskArguments+='--navigation-only'}
        & $PythonExecutable (Join-Path $repo 'tests/native_mine_target.py') @taskArguments
        if($LASTEXITCODE -ne 0){throw "Mine target task verification failed: $profile/mine-target-results.json"}
    }
    if($TcpCapacity){
        if($bound.binding.slot -ne 1){throw 'AI must reserve slot 1 of the two-slot fixture'}
        function Test-FullPacket($socket) {
            $stream=$socket.GetStream()
            $stream.ReadTimeout=1200
            $header=[byte[]]::new(3)
            $deadline=[DateTime]::UtcNow.AddMilliseconds(1200)
            do {
                try {$stream.ReadExactly($header)}catch [IO.IOException] {
                    if($_.Exception.InnerException.SocketErrorCode -eq [Net.Sockets.SocketError]::TimedOut){return $false}
                    throw
                }
                $length=[BitConverter]::ToUInt16($header,0)
                if($length -lt 3){throw "Invalid native packet length $length"}
                $payload=[byte[]]::new($length-3)
                $stream.ReadExactly($payload)
                if($header[2] -eq 2){return $true}
            } while([DateTime]::UtcNow -lt $deadline)
            return $false
        }
        $first=$null;$extra1=$null;$extra2=$null;$replacement=$null
        try {
            $first=[Net.Sockets.TcpClient]::new('127.0.0.1',$Port)
            if(Test-FullPacket $first){throw 'The only human slot was rejected'}
            $extra1=[Net.Sockets.TcpClient]::new()
            $extra2=[Net.Sockets.TcpClient]::new()
            [Threading.Tasks.Task]::WaitAll(@($extra1.ConnectAsync('127.0.0.1',$Port),$extra2.ConnectAsync('127.0.0.1',$Port)))
            if(!(Test-FullPacket $extra1) -or !(Test-FullPacket $extra2)){throw 'Extra connections were not both rejected'}
            $first.Dispose();$first=$null
            $limit=[DateTime]::UtcNow.AddSeconds(30)
            do {
                Start-Sleep -Milliseconds 200
                $replacement=[Net.Sockets.TcpClient]::new('127.0.0.1',$Port)
                if(!(Test-FullPacket $replacement)){break}
                $replacement.Dispose();$replacement=$null
            } while([DateTime]::UtcNow -lt $limit)
            if(!$replacement){throw 'Human slot was not reusable after disconnect'}
            $afterCapacity=Request @{v=1;id='after-capacity';op='state';binding=$bound.binding}
            if(!$afterCapacity.ok -or !$afterCapacity.data.active){throw 'AI binding changed during native accepts'}
        } finally {
            foreach($socket in @($first,$extra1,$extra2,$replacement)){if($socket){$socket.Dispose()}}
        }
    }
    $client.Dispose();$client=$null
    [IO.File]::WriteAllText("$profile/g81-test-stop",'The isolated harness requests native main-thread shutdown after read-only TCP assertions.')
    if(!$process.WaitForExit(20000)){throw 'Native graceful unload timed out'}
    if($process.ExitCode -ne 0){throw "Native server exited $($process.ExitCode)"}
    $exitedUtc=$process.ExitTime.ToUniversalTime()
    # A concurrently running user server owns server.log; native tML chooses
    # server2.log etc. Filter every candidate strictly by this process/world/time.
    $events=@(Get-ChildItem -LiteralPath (Join-Path $TModLoaderPath 'tModLoader-Logs') -Filter 'server*.log' | ForEach-Object {
        Get-ServerAiRunEvents -Lines (Get-Content -LiteralPath $_.FullName) -ProcessId $process.Id -WorldSession $bound.binding.world -StartedUtc $startedUtc -ExitedUtc $exitedUtc
    })
    # Persist only this process/world/time window, never search a shared log as evidence.
    $events|ForEach-Object {$_|ConvertTo-Json -Compress -Depth 12}|Set-Content -LiteralPath "$profile/server.log"
    if($Actions){
        $lateHurts=@($events | Where-Object {$_.eventName -eq 'native-hurt' })
        if($lateHurts.Count){throw 'NPC combat leaked into non-combat fixture; retain raw server.log'}
    }
    $lifecycle=Assert-ServerAiRunTeardown -Events $events
    $report=@{result='passed';mode='isolated real native dedicated server; no graphical Host & Play';binding=$bound.binding;state=$state.data;pythonClient='passed';bindingMismatchError=$bindingMismatch.error;bindingMismatchTest='request generation is current generation + 1; no native lifecycle change';nativeGenerationLifecycle='NOT RUN';actionError=$action.error;exitCode=$process.ExitCode;processId=$process.Id;startedUtc=$startedUtc.ToString('O');exitedUtc=$exitedUtc.ToString('O');logScope='process ID + bound world session + process lifetime';spawn=$lifecycle.spawn;teardown=$lifecycle.teardown;packageSHA256=(Get-FileHash -LiteralPath $package).Hash}
    if($Atomic){
        $nativeSelection=@($events|Where-Object eventName -eq 'atomic-select')
        $firstSelection=@($nativeSelection|Where-Object {$_.detail.beforeSlot -eq 0 -and $_.detail.selectedSlot -eq 9})
        if(!$firstSelection.Count){throw 'This process has no G6 native selection log evidence'}
        $report.atomicSelection=$atomicResult
        $report.nativeSelection=$firstSelection[0]
        $report.afterSelection=$after.data
    }
    if($Reads){$report.luaReads=$readResult;$report.luaCombined=$combinedResult;$report.rawPlayerQuery=$rawQuery;$report.stateAfterReads=$rawState.data;$report.readBindingMismatchError=$readMismatch.error;$report.nativeSelections=$nativeSelection}
    if($TcpCapacity){
        $reservation=@($events|Where-Object eventName -eq 'pre-listen-reservation')
        $listenerEntries=@($events|Where-Object eventName -eq 'native-listener-entry')
        if($reservation.Count -ne 1 -or !$reservation[0].detail.armed -or $reservation[0].detail.tcpListenerExists -or
            $reservation[0].detail.listenerEntries -ne 0 -or $listenerEntries.Count -ne 1 -or !$listenerEntries[0].detail.reserved){
            throw 'Reservation was not armed before the single native listener start'
        }
        $accepted=@($events|Where-Object eventName -eq 'native-accept')
        $slots=@($accepted|ForEach-Object {$_.detail.slot})
        $listeners=@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()|Where-Object {$_.Port -in @($Port,$IpcPort)})
        if($slots.Count -lt 4 -or $slots[0] -ne 0 -or $slots[-1] -ne 0 -or @($slots|Where-Object {$_ -eq -1}).Count -lt 2 -or @($slots|Where-Object {$_ -notin -1,0}).Count -or $listeners.Count){
            throw 'Native capacity slots or shutdown listener state differs from expected'
        }
        $report.tcpCapacity=@{acceptedSlots=$slots;aiSlot=$bound.binding.slot;extraDisconnectPackets=2;portsReleased=$true;clientMode='raw TCP, before Terraria handshake'}
    }
    if($Perception){$report.perception=$perceptionResult}
    if($Agent){$report.agent=$agentResult}
    if($Scene){$report.scene=Get-Content -LiteralPath "$profile/scene-actions.json" -Raw|ConvertFrom-Json}
    if($Placement){$report.placement=Get-Content -LiteralPath "$profile/placement-lua-result.json" -Raw|ConvertFrom-Json}
    if($Semantic){$report.semantic=$semanticResult}
    if($Mobility){$report.mobility=Get-Content -LiteralPath "$profile/mobility-results.json" -Raw|ConvertFrom-Json}
    if($Terrain -and (!$Mobility -or $VerticalNavigation -or $LandingRegions)){$matrixFile=if($VerticalNavigation){'vertical-result.json'}else{'terrain-result.json'};$report.terrain=Get-Content -LiteralPath "$profile/$matrixFile" -Raw|ConvertFrom-Json}
    if($VerticalNavigation -and $LandingRegions){$report.landingRegions=Get-Content -LiteralPath "$profile/terrain-result.json" -Raw|ConvertFrom-Json}
    if($Scene){$report.combatIsolation=$combatIsolation;$report.actionFixture=$fixture;$report.actionFrameMode='isolated whole native Main.Update frames, scene candidates; no old action fixture state reuse'}
    if($Tasks -or $AutonomousNavigation){$report.tasks=Get-Content -LiteralPath "$profile/mine-target-results.json" -Raw|ConvertFrom-Json}
    if($Inventory){$report.inventory=Get-Content -LiteralPath "$profile/inventory-results.json" -Raw|ConvertFrom-Json}
    if($Crafting){$report.crafting=Get-Content -LiteralPath "$profile/crafting-results.json" -Raw|ConvertFrom-Json;$report.combatIsolation=$combatIsolation}
    if($Actions -and !$Scene -and !$Crafting -and !$BenchmarkSpec){$report.combatIsolation=$combatIsolation;$report.luaActions=$actionResult;$report.nativeWorldEvidence=$worldEvidence;$report.actionFixture=$fixture;
        $report.nativeCancellations=$nativeCancellations;$report.worldAfterCancel=$worldAfterCancel;$report.emptyTileRefusal=$empty.data.action;
        $report.actionFrameMode='Explicit diagnostic whole native Main.Update frames; Netplay.HasClients remains false. Not Host & Play.'}
    if($Landing){
        $report.landingCleanup=Get-Content -LiteralPath "$profile/landing-cleanup-evidence.json" -Raw|ConvertFrom-Json
        if($report.landingCleanup.result -ne 'passed'){throw 'Native landing cleanup validation failed'}
    }
    $report|ConvertTo-Json -Depth $(if($Tasks -or $AutonomousNavigation){32}else{12})|Set-Content -LiteralPath "$profile/result.json"
    Write-Output "Native AI IPC passed: $profile/result.json"
} finally {
    if($client){$client.Dispose()}
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
    if (!$KeepProfile -and !$BenchmarkSpec -and !$Atomic) {
        try {
            $profileItem = Get-Item -LiteralPath $profile -Force
            $localRoot = [IO.Path]::GetFullPath((Join-Path $repo '.local'))
            if ($profileItem -isnot [IO.DirectoryInfo] -or $profileItem.Parent.FullName -ne $localRoot -or
                $profileItem.Name -cnotmatch '^companion-g81-native-[a-f0-9]{32}$') {
                throw "Unexpected native test profile path: $profile"
            }
            for ($ancestor = $profileItem; $ancestor; $ancestor = $ancestor.Parent) {
                if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Profile path contains a reparse point: $($ancestor.FullName)"
                }
            }
            $artifactPatterns = @{
                Worlds = '^test\.(wld|twld)(\.bak)?$|^\d{4}-\d{2}-\d{2}-test\.zip$'
                Players = '^\d{4}-\d{2}-\d{2}-character\.zip$'
                VirtualPlayerEngineProbe = '^map\.bin$|^character\.(plr|tplr)$'
                Mods = '^CompanionBridge\.tmod$'
            }
            foreach ($name in $artifactPatterns.Keys) {
                $path = Join-Path $profileItem.FullName $name
                if (!(Test-Path -LiteralPath $path)) { continue }
                $directory = Get-Item -LiteralPath $path -Force
                if ($directory -isnot [IO.DirectoryInfo] -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "Expected an ordinary test directory: $path"
                }
                foreach ($file in Get-ChildItem -LiteralPath $path -File -Recurse -Force) {
                    if ($file.Name -match $artifactPatterns[$name] -and
                        !($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
                        $file.FullName.StartsWith($directory.FullName + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                        Remove-Item -LiteralPath $file.FullName -Force
                    }
                }
            }
        } catch {
            Write-Warning "Could not clean up native test data in ${profile}: $_"
        }
    }
}
