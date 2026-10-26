param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[int]$Port=27833)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
$repo=$paths.Repository
if(!$WorldPath){$WorldPath=Join-Path $repo '.local/virtual-player-control-host/Worlds/test.wld'}
$WorldPath=(Resolve-Path -LiteralPath $WorldPath).Path
if(@(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count){throw 'Diagnostic port is in use.'}
$profile=Join-Path $repo ('.local/survival-native-validation-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds"|Out-Null
Copy-Item -LiteralPath $WorldPath -Destination "$profile/Worlds/test.wld"
$modWorld=[IO.Path]::ChangeExtension($WorldPath,'.twld')
if(Test-Path -LiteralPath $modWorld){Copy-Item -LiteralPath $modWorld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $paths.Package -Destination "$profile/Mods"
Set-Content -LiteralPath "$profile/Mods/enabled.json" -Value '["VirtualPlayerEngineProbe"]'
$arguments=@('"tModLoader.dll"','-server','-world',('"'+$profile+'/Worlds/test.wld"'),'-port',"$Port",
    '-tmlsavedirectory',('"'+$profile+'"'),'-modpath',('"'+$profile+'/Mods"'),'-survivalselftest')
$process=Start-Process -FilePath (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') -ArgumentList $arguments `
    -WorkingDirectory $TModLoaderPath -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput "$profile/stdout.log" -RedirectStandardError "$profile/stderr.log"
try{
    $null=$process.Handle
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    $resultPath=Join-Path $profile 'survival-native-result.json'
    while(!$process.HasExited -and !(Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 300}
    Write-Output "Native diagnostic profile: $profile"
    if(!(Test-Path -LiteralPath $resultPath)){throw 'Native survival test did not produce a result.'}
    $result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
    if($result.result -ne 'passed'){throw (Get-Content -LiteralPath $resultPath -Raw)}
    if(!$process.WaitForExit(30000)){throw 'Native selftest did not finish within 30 seconds.'}
    $process.WaitForExit();$process.Refresh()
    if($process.ExitCode -ne 0){throw ('Native selftest exit code: '+$process.ExitCode)}
    Write-Output (Get-Content -LiteralPath $resultPath -Raw)
}finally{
    # Only terminate the process created above for this new diagnostic profile.
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    $process.Dispose()
}
