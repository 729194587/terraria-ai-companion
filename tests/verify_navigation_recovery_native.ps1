param([string]$TModLoaderPath,[string]$EngineProfile,
    [string]$WorldPath, [string]$PackagePath, [int]$Port=27991, [int]$IpcPort=27992, [switch]$Baseline,[string[]]$Scenes,[switch]$Mobility,[switch]$Probe,[switch]$Region,
    [ValidateRange(1,20)][int]$RegionIterations=8,[switch]$Environment,[switch]$AutonomousNavigation,[switch]$ExtraJump)
$ErrorActionPreference='Stop'
if($ExtraJump){$Mobility=$true}
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
if($Environment -and !$Region){throw 'Environment probe requires Region mode.'}
if($AutonomousNavigation -and ($Region -or $Mobility -or $Baseline -or $Probe)){throw 'Autonomous recovery uses the native recovery scenes.'}
if($Region -and ($Mobility -or $Probe -or $Baseline -or $Scenes)){throw 'Region mode does not combine with mobility/recovery probes or scene selectors.'}
$repo=Split-Path $PSScriptRoot -Parent
$world=(Resolve-Path -LiteralPath $WorldPath).Path
$package=(Resolve-Path -LiteralPath $PackagePath).Path
Assert-CompanionPackage -Package $package -Engine $selectedEngine

if($Port -eq $IpcPort -or @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
    Where-Object {$_.Port -in @($Port,$IpcPort)}).Count){throw 'Test ports must be distinct and unused'}
$profile=Join-Path $repo ('.local/navigation-recovery-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
Copy-Item -LiteralPath $world -Destination "$profile/Worlds/test.wld"
$twld=[IO.Path]::ChangeExtension($world,'.twld')
if(Test-Path -LiteralPath $twld){Copy-Item -LiteralPath $twld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')
[IO.File]::WriteAllText("$profile/isolated-action-fixture",'M4.2 copied-world diagnostic disturbances only')
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
foreach($arg in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,
    '-modpath',"$profile/Mods",'-companionserverai','-companionengineprofile',$selectedEngine.Name,'-companionaiport',"$IpcPort",'-companionaiipctest',
    '-companionactionstest','-companionterraintest','-companionnavrecoverytest')){$info.ArgumentList.Add($arg)}
if($Mobility -or $Region){$info.ArgumentList.Add('-companionmobilitytest')}
if($ExtraJump){$info.ArgumentList.Add('-companionextrajumptest')}
if($Region){$info.ArgumentList.Add('-companionregiontest')}
if($Environment){$info.ArgumentList.Add('-companionenvironmenttest')}
$process=[Diagnostics.Process]::Start($info)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try {
    $testScript=if($ExtraJump){'native_extra_jump.py'}elseif($Region){'native_scene_region.py'}elseif($Mobility){'native_mobility.py'}else{'native_navigation_recovery.py'}
    $probeArgs=@('-B',(Join-Path $PSScriptRoot $testScript),'--port',"$IpcPort",'--profile',$profile)
    if($Region){$probeArgs+=@('--iterations',"$RegionIterations")}
    if($Environment){$probeArgs+='--environment'}
    if($Probe){$probeArgs+='--probe'}
    if($Baseline){$probeArgs+='--baseline'}
    if($Scenes){$probeArgs+=@('--scenes')+$Scenes}
    if($AutonomousNavigation){$probeArgs+='--autonomous'}
    & "$repo/.local/lua-venv/Scripts/python.exe" @probeArgs
    if($LASTEXITCODE -ne 0){throw "Native recovery verification failed: $profile"}
    if($Mobility){
        $discovery=Get-Content -LiteralPath "$profile/movement-discovery.json" -Raw | ConvertFrom-Json
        if($discovery.checks -lt 100 -or !$discovery.fieldInjectionRestored -or $discovery.additionalPlayerUpdates -ne 0){throw 'Movement discovery boundary checks failed'}
        Write-Output "Movement discovery native: $($discovery.checks) checks at $($discovery.boundary)"
    }
    [IO.File]::WriteAllText("$profile/g81-test-stop",'Stop only this isolated server')
    if(!$process.WaitForExit(20000) -or $process.ExitCode -ne 0){throw 'Isolated server did not unload cleanly'}
    @{profile=$profile;packageSHA256=(Get-FileHash -LiteralPath $package).Hash;
        sourceWorldSHA256=(Get-FileHash -LiteralPath $world).Hash;processId=$process.Id;exitCode=$process.ExitCode;
        baseline=[bool]$Baseline;mode='isolated native copied world; no clients or models'} |
        ConvertTo-Json | Set-Content "$profile/manifest.json"
    Write-Output "Native recovery results: $profile"
} finally {
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
