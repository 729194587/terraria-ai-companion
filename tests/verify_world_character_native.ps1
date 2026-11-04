param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[Parameter(Mandatory)][string]$PackagePath,[int]$Port=27961)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$WorldPath){$WorldPath=Join-Path $repo '.local/companion-g82-host-20261027-175538-078/Worlds/test.wld'}
$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path
$package=(Resolve-Path -LiteralPath $PackagePath).Path
foreach($candidatePort in @($Port,($Port+1))){if(@(Get-NetTCPConnection -LocalPort $candidatePort -State Listen -ErrorAction SilentlyContinue).Count){throw 'Native fixture port occupied'}}
$sourceHash=(Get-FileHash -LiteralPath $WorldPath).Hash
$profile=Join-Path $repo ('.local/world-character-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
$worldMod=[IO.Path]::ChangeExtension($WorldPath,'.twld')
if(Test-Path -LiteralPath $worldMod){Copy-Item -LiteralPath $worldMod -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
Set-Content -LiteralPath "$profile/Mods/enabled.json" -Value '["CompanionBridge"]'
Set-Content -LiteralPath "$profile/isolated-world-character-fixture" -Value 'private headless diagnostic only'
$resultPath=Join-Path $profile 'world-character-result.json'
$identity=$null;$pointer=$null
foreach($round in 1..5)
{
    if($round -eq 5)
    {
        [IO.File]::SetAttributes($pointer,[IO.FileAttributes]::Normal)
        $corrupt=Get-Content -LiteralPath $pointer -Raw|ConvertFrom-Json;$corrupt.Version=999
        $corrupt|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $pointer
        $corruptHash=(Get-FileHash -LiteralPath $pointer).Hash
    }
    $arguments=@('"tModLoader.dll"','-server','-world',('"'+$profile+'/Worlds/test.wld"'),'-port',"$Port",'-tmlsavedirectory',('"'+$profile+'"'),'-modpath',('"'+$profile+'/Mods"'),'-companionserverai','-companionaiport',"$($Port+1)",'-worldcharacterselftest','-worldcharacterround',"$round")
    $process=Start-Process -FilePath (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') -ArgumentList $arguments -WorkingDirectory $TModLoaderPath -WindowStyle Hidden -PassThru -RedirectStandardOutput "$profile/round-$round.stdout.log" -RedirectStandardError "$profile/round-$round.stderr.log"
    try
    {
        $deadline=[DateTime]::UtcNow.AddSeconds(90)
        while(!(Test-Path -LiteralPath $resultPath) -and !$process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 200}
        if(!(Test-Path -LiteralPath $resultPath)){throw "Native result missing, round $round; $profile"}
        $r=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
        $expected=if($round -le 3){'world-character-native-passed'}elseif($round -eq 4){'save-failed-retained'}else{'corrupt-load-rejected'}
        if($r.result -ne $expected){throw "Native round $round failed: $(Get-Content -LiteralPath $resultPath -Raw)"}
        if($round -eq 1){if($r.profileId -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid diagnostic profile identity'};$identity=$r.profileId;$pointer=Join-Path $profile "VirtualPlayerEngineProbe/Characters/$identity/world.json"}
        if($round -le 4 -and $r.profileId -ne $identity){throw 'World character identity changed across processes'}
        if($round -eq 5 -and ($r.actorPublished -or (Get-FileHash -LiteralPath $pointer).Hash -ne $corruptHash)){throw 'Corrupt load published actor or modified data'}
        Copy-Item -LiteralPath $resultPath -Destination "$profile/round-$round-result.json"
        Copy-Item -LiteralPath $pointer -Destination "$profile/round-$round-world-manifest.json"
        Remove-Item -LiteralPath $resultPath
        if(!$process.WaitForExit(10000) -and $round -le 3){throw 'Positive native process did not exit'}
        if($round -le 3){$process.Refresh();if($process.ExitCode -ne 0){throw 'Positive native process exit failed'}}
    }
    finally
    {
        # Only this private diagnostic process is ours; never touch user games.
        if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
        $process.Dispose()
    }
}
if((Get-FileHash -LiteralPath $WorldPath).Hash -ne $sourceHash){throw 'Source world changed'}
Write-Output "Native world-character lifecycle passed across five processes: $profile"
Write-Output 'Host & Play and real models: NOT EXECUTED.'
