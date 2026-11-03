param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[int]$Port=27971,[int]$IpcPort=27972,[string]$PackagePath,[string]$PythonExecutable,[string]$HelperSource,[string]$Seed='20261028',[int[]]$Offsets=@(0,32,-32,64,-64),[switch]$LandingRegions,[switch]$Scene,[switch]$Trip)
$ErrorActionPreference='Stop'
if($Port -eq $IpcPort){throw 'Native game and AI IPC test ports must be distinct.'}
$occupied=@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {$_.Port -in @($Port,$IpcPort)})
if($occupied.Count){throw 'Native test port already occupied; refusing to connect to an unrelated process.'}
. (Join-Path $PSScriptRoot 'server_ai_native_evidence.ps1')
$repo=Split-Path $PSScriptRoot -Parent
if($WorldPath){$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path}
$package=if($PackagePath){(Resolve-Path -LiteralPath $PackagePath).Path}else{Join-Path $repo '.local/natural-build/Mods/CompanionBridge.tmod'}
if(!$PythonExecutable){$PythonExecutable=Join-Path $repo '.local/lua-venv/Scripts/python.exe'}
if(!(Test-Path -LiteralPath $package)){throw 'Build CompanionBridge first.'}
$profile=Join-Path $repo ('.local/natural-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
$oldLogs=Join-Path $TModLoaderPath 'tModLoader-Logs'
if(Test-Path -LiteralPath $oldLogs){Copy-Item -LiteralPath $oldLogs -Destination "$profile/previous-installation-logs" -Recurse}
if($WorldPath){
    Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
    $modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
    if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')
[IO.File]::WriteAllText("$profile/isolated-natural-world",'Native generated-terrain navigation diagnostic; no Tile writes.')
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
foreach($argument in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionaiport',"$IpcPort",'-companionaiipctest')){$info.ArgumentList.Add($argument)}
$info.ArgumentList.Add('-companionnaturaltest')
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
    $probeScript=if($Trip){'tests/native_trip_navigation.py'}elseif($Scene){'tests/native_natural_scene.py'}elseif($LandingRegions){'tests/native_natural_landing.py'}else{'tests/native_natural_navigation.py'}
    $probeArgs=@((Join-Path $repo $probeScript),'--port',"$IpcPort",'--profile',$profile,'--offsets')+@($Offsets|ForEach-Object {"$_"})
    if($HelperSource){$probeArgs+=@('--helper-source',(Resolve-Path -LiteralPath $HelperSource).Path)}
    & $PythonExecutable @probeArgs
    if($LASTEXITCODE -ne 0){throw 'Natural deterministic Lua probe failed'}
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
    $report=@{mode='native generated terrain; no constructed matrix/no LLM';sourceWorld=$WorldPath;initialWorldSHA256=$initialWorldHash;packageSHA256=(Get-FileHash -LiteralPath $package).Hash;binding=$bound.binding;spawn=$lifecycle.spawn;teardown=$lifecycle.teardown;results=(Get-Content -LiteralPath "$profile/natural-results.json" -Raw|ConvertFrom-Json)}
    $report|ConvertTo-Json -Depth 60|Set-Content -LiteralPath "$profile/result.json"
    Write-Output "Natural native results: $profile/result.json"
} finally {
    if($client){$client.Dispose()}
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
