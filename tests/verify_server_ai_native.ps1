param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[int]$Port=27841,[int]$IpcPort=27842,[string]$PackagePath,[switch]$Atomic,[switch]$Reads,[switch]$Actions,[string]$PythonExecutable)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'server_ai_native_evidence.ps1')
$repo=Split-Path $PSScriptRoot -Parent
if(!$WorldPath){$WorldPath=Join-Path $repo '.local/virtual-player-control-host/Worlds/test.wld'}
$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path
$package=if($PackagePath){(Resolve-Path -LiteralPath $PackagePath).Path}else{Join-Path $repo '.local/companion-g81-build/Mods/CompanionBridge.tmod'}
if(!$PythonExecutable){$PythonExecutable=if($Atomic){Join-Path $repo '.local/lua-venv/Scripts/python.exe'}else{'python'}}
if(!(Test-Path -LiteralPath $package)){throw 'Build CompanionBridge first.'}
$profile=Join-Path $repo ('.local/companion-'+$(if($Atomic){'g82'}else{'g81'})+'-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
$oldLogs=Join-Path $TModLoaderPath 'tModLoader-Logs'
if(Test-Path -LiteralPath $oldLogs){Copy-Item -LiteralPath $oldLogs -Destination "$profile/previous-installation-logs" -Recurse}
Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
$modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')
if($Actions){[IO.File]::WriteAllText("$profile/isolated-action-fixture",'Explicit test fixture in this copied world only.')}
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
foreach($argument in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionaiport',"$IpcPort",'-companionaiipctest')){$info.ArgumentList.Add($argument)}
if($Actions){$info.ArgumentList.Add('-companionactionstest')}
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
    $state=Request @{v=1;id='native-state';op='state';binding=$bound.binding}
    if(!$state.ok -or !$state.data.active -or $state.data.inventory.Count -lt 50 -or $bound.binding.generation -lt 1){throw 'Native state contract failed'}
    $mismatchedBinding=@{actor=$bound.binding.actor;world=$bound.binding.world;generation=([long]$bound.binding.generation+1);slot=$bound.binding.slot}
    $bindingMismatch=Request @{v=1;id='native-binding-mismatch';op='state';binding=$mismatchedBinding}
    if($bindingMismatch.ok -or $bindingMismatch.error -ne 'stale_binding'){throw 'Mismatched request binding was not rejected'}
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
    if($Actions){
        $fixturePath=Join-Path $profile 'action-fixture.json'
        $fixtureDeadline=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath $fixturePath) -and [DateTime]::UtcNow -lt $fixtureDeadline){Start-Sleep -Milliseconds 100}
        $fixture=Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
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
        $nativeCancellations=@{}
        $nativeCancellations.navigation=Cancel-RunningNativeAction 'navigate_to_x' @{targetX=($fixture.targetX+32)}
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
    if($Actions){$report.luaActions=$actionResult;$report.nativeWorldEvidence=$worldEvidence;$report.actionFixture=$fixture;
        $report.nativeCancellations=$nativeCancellations;$report.worldAfterCancel=$worldAfterCancel;$report.emptyTileRefusal=$empty.data.action;
        $report.actionFrameMode='Explicit diagnostic whole native Main.Update frames; Netplay.HasClients remains false. Not Host & Play.'}
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath "$profile/result.json"
    Write-Output "Native AI IPC passed: $profile/result.json"
} finally {
    if($client){$client.Dispose()}
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
