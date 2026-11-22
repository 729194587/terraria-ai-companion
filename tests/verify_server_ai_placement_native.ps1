param([string]$TModLoaderPath,[string]$EngineProfile,[string]$WorldPath,[int]$Port=27837,[switch]$Failure,[switch]$Object,[switch]$Survey,[switch]$AfterPlacementException,[switch]$ItemUse,
    [string]$PackagePath,[ValidateSet('VirtualPlayerEngineProbe','CompanionBridge')][string]$ModName='CompanionBridge')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
if($AfterPlacementException -and (!$Object -or !$Failure)){throw 'AfterPlacementException requires Object and Failure.'}
$repo=Split-Path $PSScriptRoot -Parent
if(!$WorldPath){$WorldPath=Join-Path $repo '.local/virtual-player-control-host/Worlds/test.wld'}
$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path
if(@(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count){throw 'Diagnostic port is in use.'}
$profile=Join-Path $repo ('.local/placement-native-validation-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds"|Out-Null
Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
$modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
$package=if($PackagePath){(Resolve-Path -LiteralPath $PackagePath).Path}else{$selectedEngine.Package}
Assert-CompanionPackage -Package $package -Engine $selectedEngine
Copy-Item -LiteralPath $package -Destination "$profile/Mods/$ModName.tmod"
Set-Content -LiteralPath "$profile/Mods/enabled.json" -Value ('["'+$ModName+'"]')
$arguments=@('"tModLoader.dll"','-server','-world',('"'+$profile+'/Worlds/test.wld"'),'-port',"$Port",
    '-tmlsavedirectory',('"'+$profile+'"'),'-modpath',('"'+$profile+'/Mods"'),'-placementselftest')
if($ItemUse){$arguments[$arguments.Length-1]='-itemuseselftest'}
if($Failure){$arguments+=$(if($ItemUse){'-itemusefailureselftest'}else{'-placementfailureselftest'})}
if($Survey){$arguments+='-placementsurveyselftest'}
if($Object){$arguments+='-objectplacementselftest'}
if($AfterPlacementException){$arguments+='-objectplacementafterexception'}
if($ModName -eq 'CompanionBridge'){
    $ipcPort=$Port+1
    if(@(Get-NetTCPConnection -LocalPort $ipcPort -State Listen -ErrorAction SilentlyContinue).Count){throw 'Diagnostic IPC port is in use.'}
    $arguments+=@('-companionserverai','-companionengineprofile',$selectedEngine.Name,'-companionaiport',"$ipcPort")
}
$process=Start-Process -FilePath (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') -ArgumentList $arguments `
    -WorkingDirectory $TModLoaderPath -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput "$profile/stdout.log" -RedirectStandardError "$profile/stderr.log"
try{
    $null=$process.Handle
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    $resultPath=Join-Path $profile $(if($ItemUse){'item-use-native-result.json'}else{'placement-native-result.json'})
    while(!$process.HasExited -and !(Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 300}
    Write-Output "Native diagnostic profile: $profile"
    if(!(Test-Path -LiteralPath $resultPath)){throw 'Native placement test did not produce a result.'}
    $result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
    if($result.result -ne 'passed'){throw (Get-Content -LiteralPath $resultPath -Raw)}
    if($result.processTerminationRequired){
        if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
        Write-Output 'Quarantine assertions passed; terminated only the created diagnostic process (graceful recovery is intentionally forbidden).'
        Write-Output (Get-Content -LiteralPath $resultPath -Raw)
        return
    }
    if(!$process.WaitForExit(30000)){throw 'Native selftest did not exit within 30 seconds.'}
    $process.WaitForExit()
    $process.Refresh()
    if($process.ExitCode -ne 0){throw ('Native selftest exit code: '+$process.ExitCode)}
    Write-Output (Get-Content -LiteralPath $resultPath -Raw)
}finally{
    # Only terminate the process created above for this new diagnostic profile.
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    $process.Dispose()
}
