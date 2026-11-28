param([string]$TModLoaderPath,[string]$EngineProfile,[string]$WorldPath,[int]$Port=27971,[int]$IpcPort=27972,[string]$PackagePath,[string]$PythonExecutable,[string]$HelperSource,[string]$Seed='20261028',[int[]]$Offsets=@(0,32,-32,64,-64),[switch]$Mining,[string[]]$MiningCases,[ValidateSet('On','Off')][string]$ImproveGameVeinMining,[string]$WorkshopRoot='D:/steam/steamapps/workshop/content/1281930',[switch]$LandingRegions,[switch]$Scene,[switch]$Trip,[switch]$AutonomousNavigation,[switch]$LongJourney,[ValidateSet("basic","cloud","sand","blizzard","angel","cloud_angel","hermes","bundle","bundle_hermes")][string]$JourneyLoadout,[string]$ReplayStops,[switch]$WalkReference,[switch]$FlightReference,[switch]$Watch,[switch]$ShoreStarts,[string]$WatchWestStop,[switch]$WestDescent,[switch]$EastWall,[ValidateSet("east","west","central","traverse","bank-replay","local-replay","slope-entry","slope-approach")][string]$JourneyCase,[switch]$TravelContact,[switch]$DeathFeedback,[switch]$BroadcastFeedback,[ValidateSet("v2","directional","driver")][string]$ExplorationAlgorithm,[ValidateRange(10,600)][int]$ExplorationSeconds=300,[string]$ExplorationSeat)
$ErrorActionPreference='Stop'
if($ImproveGameVeinMining){$Mining=$true;$MiningCases=@('small-copper')}
if($FlightReference){$JourneyLoadout="cloud_angel";$JourneyCase="traverse"}
if($JourneyLoadout){$LongJourney=$true}
if($WatchWestStop){$Watch=$true}
if($Watch -and ($WalkReference -or $ReplayStops)){throw "Watch runs live navigation journeys."}
if($BroadcastFeedback -or $DeathFeedback -or $TravelContact -or $JourneyCase -or $EastWall -or $WestDescent -or $ShoreStarts -or $Watch -or $ReplayStops -or $WalkReference){$LongJourney=$true}
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
if($Port -eq $IpcPort){throw 'Native game and AI IPC test ports must be distinct.'}
$occupied=@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {$_.Port -in @($Port,$IpcPort)})
if($occupied.Count){throw 'Native test port already occupied; refusing to connect to an unrelated process.'}
. (Join-Path $PSScriptRoot 'server_ai_native_evidence.ps1')
$repo=Split-Path $PSScriptRoot -Parent
if($WorldPath){$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path}
$package=if($PackagePath){(Resolve-Path -LiteralPath $PackagePath).Path}else{$selectedEngine.Package}
Assert-CompanionPackage -Package $package -Engine $selectedEngine
if(!$PythonExecutable){$PythonExecutable=Join-Path $repo '.local/lua-venv/Scripts/python.exe'}
if(!(Test-Path -LiteralPath $package)){throw 'Build CompanionBridge first.'}
$profile=Join-Path $repo ('.local/natural-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
if($WorldPath){
    Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
    $modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
    if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
$enabledMods=@('CompanionBridge')
if($ImproveGameVeinMining){
    foreach($file in @('2797518634/2026.3/ImproveGame.tmod','3403460014/2026.3/SilkyUIFramework.tmod')){
        Copy-Item -LiteralPath (Join-Path $WorkshopRoot $file) -Destination "$profile/Mods"
        $enabledMods += [IO.Path]::GetFileNameWithoutExtension($file)
    }
    New-Item -ItemType Directory "$profile/ModConfigs" | Out-Null
    @{SimpleVeinMining=($ImproveGameVeinMining -eq 'On')} | ConvertTo-Json | Set-Content "$profile/ModConfigs/ImproveGame_ImproveConfigs.json"
}
[IO.File]::WriteAllText("$profile/Mods/enabled.json",(ConvertTo-Json -InputObject $enabledMods -Compress))
[IO.File]::WriteAllText("$profile/isolated-natural-world",'Native generated-terrain navigation diagnostic; no Tile writes.')
if($FlightReference){[IO.File]::WriteAllText("$profile/flight-reference","Continuous right, full cloud jump and wing flight")}
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
foreach($argument in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionengineprofile',$selectedEngine.Name,'-companionaiport',"$IpcPort",'-companionaiipctest')){$info.ArgumentList.Add($argument)}
if($Watch){foreach($a in @('-ip','127.0.0.1','-companionwatchtest')){$info.ArgumentList.Add($a)}}
$info.ArgumentList.Add('-companionnaturaltest')
if($ExplorationAlgorithm){$info.ArgumentList.Add('-companionexplorationfield')}
if($Mining){$info.ArgumentList.Add('-companionnaturalminingtest')}
if($JourneyCase -in @("traverse","local-replay","slope-entry","slope-approach")){$info.ArgumentList.Add("-companiontraversetest")}
if($JourneyLoadout){$info.ArgumentList.Add("-companionjourneyloadout");$info.ArgumentList.Add($JourneyLoadout)}
if($LongJourney){$info.ArgumentList.Add('-companionjourneytest')}
if(!$WorldPath){foreach($argument in @('-autocreate','1','-seed',$Seed,'-worldname','NaturalExploration','-difficulty','0')){$info.ArgumentList.Add($argument)}}
$process=[Diagnostics.Process]::Start($info)
$startedUtc=$process.StartTime.ToUniversalTime()
$stdout=$process.StandardOutput.ReadToEndAsync()
$stderr=$process.StandardError.ReadToEndAsync()
$client=$null
try {
    $deadline=[DateTime]::UtcNow.AddSeconds($(if($WorldPath){90}else{300}))
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
    $loadedWorld=if($WorldPath){Join-Path $profile 'Worlds/test.wld'}else{
        $generated=@(Get-ChildItem -LiteralPath "$profile/Worlds" -Filter '*.wld')
        if($generated.Count -ne 1){throw 'Generated world file is ambiguous'}
        $generated[0].FullName
    }
    Copy-Item -LiteralPath $loadedWorld -Destination "$profile/source.wld"
    $initialWorldHash=(Get-FileHash -LiteralPath "$profile/source.wld").Hash
    $probeScript=if($Mining){'tests/native_natural_mining.py'}elseif($LongJourney){'tests/native_natural_journey.py'}elseif($AutonomousNavigation){'tests/native_natural_landing.py'}elseif($Trip){'tests/native_trip_navigation.py'}elseif($Scene){'tests/native_natural_scene.py'}elseif($LandingRegions){'tests/native_natural_landing.py'}else{'tests/native_natural_navigation.py'}
    $probeArgs=@((Join-Path $repo $probeScript),'--port',"$IpcPort",'--profile',$profile,'--offsets')+@($Offsets|ForEach-Object {"$_"})
    if($LongJourney -or $Mining){$probeArgs=@((Join-Path $repo $probeScript),'--port',"$IpcPort",'--profile',$profile)}
    if($ExplorationAlgorithm){
        $probeArgs=@((Join-Path $repo 'tests/native_exploration_v2.py'),'--port',"$IpcPort",'--profile',$profile,
                     '--algorithm',$ExplorationAlgorithm,'--seconds',"$ExplorationSeconds")
    }
    if($ExplorationAlgorithm -eq 'driver'){
        $probeArgs+=@('--seat',$ExplorationSeat)
        $client.Dispose();$client=$null
    }
    if($ImproveGameVeinMining){$probeArgs+=@('--vein-mining',$ImproveGameVeinMining)}
    if($MiningCases){$probeArgs+=@('--cases')+$MiningCases}
    if($Watch){
        $viewer=Join-Path $profile 'viewer'
        New-Item -ItemType Directory -Path "$viewer/Mods" | Out-Null
        Copy-Item -LiteralPath "$profile/Mods/CompanionBridge.tmod","$profile/Mods/enabled.json" -Destination "$viewer/Mods"
        [IO.File]::WriteAllText("$viewer/isolated-watch-viewer",'Disposable local observer')
        $viewerArgs=@('-tmlsavedirectory',('"{0}"' -f $viewer),'-modpath',('"{0}/Mods"' -f $viewer),
            '-join','127.0.0.1','-port',"$Port",'-plr',('"{0}/Players/Observer.plr"' -f $viewer),'-companionwatchviewer')
        Start-Process -FilePath (Join-Path $TModLoaderPath 'start-tModLoader.bat') -WorkingDirectory $TModLoaderPath -ArgumentList $viewerArgs
        Write-Output "Waiting for the game camera at 127.0.0.1:$Port"
        $probeArgs+='--watch'
        if($WatchWestStop){$probeArgs+=@('--watch-west-stop',(Resolve-Path -LiteralPath $WatchWestStop).Path)}
    }
    if($BroadcastFeedback){$probeArgs+="--broadcast-feedback"}
    if($DeathFeedback){$probeArgs+="--death-feedback"}
    if($TravelContact){$probeArgs+="--travel-contact"}
    if($JourneyCase){$probeArgs+=@("--journey-case",$JourneyCase)}
    if($EastWall){$probeArgs+="--east-wall"}
    if($WestDescent){$probeArgs+="--west-descent"}
    if($ShoreStarts){$probeArgs+="--shore-starts"}
    if($WalkReference){$probeArgs+='--walk-reference'}
    if($ReplayStops){$probeArgs+=@('--replay-stops',(Resolve-Path -LiteralPath $ReplayStops).Path)}
    if($HelperSource){$probeArgs+=@('--helper-source',(Resolve-Path -LiteralPath $HelperSource).Path)}
    if($AutonomousNavigation){$probeArgs+='--autonomous'}
    & $PythonExecutable @probeArgs
    if($LASTEXITCODE -ne 0){throw 'Natural deterministic Lua probe failed'}
    if($client){$client.Dispose();$client=$null}
    [IO.File]::WriteAllText("$profile/g81-test-stop",'The isolated harness requests native main-thread shutdown after read-only TCP assertions.')
    if(!$process.WaitForExit(20000)){throw 'Native graceful unload timed out'}
    if($process.ExitCode -ne 0){throw "Native server exited $($process.ExitCode)"}
    $exitedUtc=$process.ExitTime.ToUniversalTime()
    # A concurrently running user server owns server.log; native tML chooses
    # server2.log etc. Filter every candidate strictly by this process/world/time.
    $events=@(Get-ChildItem -LiteralPath (Join-Path $TModLoaderPath 'tModLoader-Logs') -Filter 'server*.log' | ForEach-Object {
        try {$lines=Get-Content -LiteralPath $_.FullName}
        catch [System.Management.Automation.ItemNotFoundException] {return} # Another isolated server rotated this file.
        Get-ServerAiRunEvents -Lines $lines -ProcessId $process.Id -WorldSession $bound.binding.world -StartedUtc $startedUtc -ExitedUtc $exitedUtc
    })
    # Persist only this process/world/time window, never search a shared log as evidence.
    $events|ForEach-Object {$_|ConvertTo-Json -Compress -Depth 12}|Set-Content -LiteralPath "$profile/server.log"
    $lifecycle=Assert-ServerAiRunTeardown -Events $events
    $report=@{mode='native generated terrain; no constructed matrix/no LLM';journeyLoadout=$JourneyLoadout;sourceWorld=$WorldPath;initialWorldSHA256=$initialWorldHash;packageSHA256=(Get-FileHash -LiteralPath $package).Hash;binding=$bound.binding;spawn=$lifecycle.spawn;teardown=$lifecycle.teardown;results=(Get-Content -LiteralPath "$profile/natural-results.json" -Raw|ConvertFrom-Json)}
    if($JourneyLoadout){
        foreach($scenario in $report.results.scenarios){
            if($null -eq $scenario.cloudEnabled -or $null -eq $scenario.wingSlot -or
                $scenario.cloudEnabled -ne ($JourneyLoadout -in @("cloud","cloud_angel","bundle","bundle_hermes")) -or
                ($JourneyLoadout -in @("hermes","bundle_hermes") -and !$scenario.hermesEquipped) -or
                ($JourneyLoadout -in @("sand","bundle","bundle_hermes") -and !$scenario.sandEnabled) -or
                ($JourneyLoadout -in @("blizzard","bundle","bundle_hermes") -and !$scenario.blizzardEnabled) -or
                $scenario.wingSlot -ne $(if($JourneyLoadout -in @("angel","cloud_angel")){2}else{0})){
                throw "Natural trace does not confirm the requested loadout: $JourneyLoadout"
            }
        }
    }
    $report|ConvertTo-Json -Depth 60|Set-Content -LiteralPath "$profile/result.json"
    Write-Output "Natural native results: $profile/result.json"
} finally {
    if($client){$client.Dispose()}
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
