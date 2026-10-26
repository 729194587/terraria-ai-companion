param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[int]$Port=27841,[int]$IpcPort=27842)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'server_ai_native_evidence.ps1')
$repo=Split-Path $PSScriptRoot -Parent
if(!$WorldPath){$WorldPath=Join-Path $repo '.local/virtual-player-control-host/Worlds/test.wld'}
$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path
$package=Join-Path $repo '.local/companion-g81-build/Mods/CompanionBridge.tmod'
if(!(Test-Path -LiteralPath $package)){throw 'Build CompanionBridge first.'}
$profile=Join-Path $repo ('.local/companion-g81-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
$oldLogs=Join-Path $TModLoaderPath 'tModLoader-Logs'
if(Test-Path -LiteralPath $oldLogs){Copy-Item -LiteralPath $oldLogs -Destination "$profile/previous-installation-logs" -Recurse}
Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
$modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
foreach($argument in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionaiport',"$IpcPort",'-companionaiipctest')){$info.ArgumentList.Add($argument)}
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
    $pythonText=& python (Join-Path $repo 'scripts/ai_player_state.py') --port $IpcPort
    if($LASTEXITCODE -ne 0){throw 'Python read-only client failed'}
    $pythonState=($pythonText -join "`n")|ConvertFrom-Json
    if(!$pythonState.ok -or $pythonState.binding.actor -ne $bound.binding.actor){throw 'Python client returned a different actor identity'}
    $client.Dispose();$client=$null
    [IO.File]::WriteAllText("$profile/g81-test-stop",'The isolated harness requests native main-thread shutdown after read-only TCP assertions.')
    if(!$process.WaitForExit(20000)){throw 'Native graceful unload timed out'}
    if($process.ExitCode -ne 0){throw "Native server exited $($process.ExitCode)"}
    $exitedUtc=$process.ExitTime.ToUniversalTime()
    $nativeLog=Join-Path $TModLoaderPath 'tModLoader-Logs/server.log'
    $events=@(Get-ServerAiRunEvents -Lines (Get-Content -LiteralPath $nativeLog) -ProcessId $process.Id -WorldSession $bound.binding.world -StartedUtc $startedUtc -ExitedUtc $exitedUtc)
    # Persist only this process/world/time window, never search a shared log as evidence.
    $events|ForEach-Object {$_|ConvertTo-Json -Compress -Depth 12}|Set-Content -LiteralPath "$profile/server.log"
    $lifecycle=Assert-ServerAiRunTeardown -Events $events
    $report=@{result='passed';mode='isolated real native dedicated server; no graphical Host & Play';binding=$bound.binding;state=$state.data;pythonClient='passed';bindingMismatchError=$bindingMismatch.error;bindingMismatchTest='request generation is current generation + 1; no native lifecycle change';nativeGenerationLifecycle='NOT RUN';actionError=$action.error;exitCode=$process.ExitCode;processId=$process.Id;startedUtc=$startedUtc.ToString('O');exitedUtc=$exitedUtc.ToString('O');logScope='process ID + bound world session + process lifetime';spawn=$lifecycle.spawn;teardown=$lifecycle.teardown;packageSHA256=(Get-FileHash -LiteralPath $package).Hash}
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath "$profile/result.json"
    Write-Output "Native AI IPC passed: $profile/result.json"
} finally {
    if($client){$client.Dispose()}
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
