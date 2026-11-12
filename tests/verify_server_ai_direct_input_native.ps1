param([string]$TModLoaderPath,[string]$EngineProfile, [string]$WorldPath='.local/virtual-player-control-host/Worlds/test.wld', [string]$PackagePath, [int]$Port=27941, [int]$IpcPort=27942, [string]$TestScript='native_direct_input.py', [switch]$Scene, [switch]$Equipment, [switch]$Torch)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
if(!$PackagePath){$PackagePath=$selectedEngine.Package}
$repo=Split-Path $PSScriptRoot -Parent
if($Port -eq $IpcPort -or @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {$_.Port -in @($Port,$IpcPort)}).Count){throw 'Test ports must be distinct and unused'}
$world=(Resolve-Path -LiteralPath $WorldPath).Path
$package=(Resolve-Path -LiteralPath $PackagePath).Path
Assert-CompanionPackage -Package $package -Engine $selectedEngine
$profile=Join-Path $repo ('.local/direct-input-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
Copy-Item -LiteralPath $world -Destination "$profile/Worlds/test.wld"
$twld=[IO.Path]::ChangeExtension($world,'.twld')
if(Test-Path -LiteralPath $twld){Copy-Item -LiteralPath $twld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')
[IO.File]::WriteAllText("$profile/isolated-action-fixture",'Explicit copied-world diagnostic only')
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
foreach($arg in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionengineprofile',$selectedEngine.Name,'-companionaiport',"$IpcPort",'-companionaiipctest','-companionactionstest')){$info.ArgumentList.Add($arg)}
if($Torch){$info.ArgumentList.Add('-companiontorchtest');$TestScript='native_auto_torch.py'}
elseif($Equipment){$info.ArgumentList.Add('-companionequipmenttest');$TestScript='native_equipment.py'}
elseif($Scene){$info.ArgumentList.Add('-companionscenetest');$info.ArgumentList.Add('-companionperceptiontest');$info.ArgumentList.Add('-companionscenelighttest')}
else{$info.ArgumentList.Add('-companiondirectinputtest')}
$process=[Diagnostics.Process]::Start($info)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try {
    & "$repo/.local/lua-venv/Scripts/python.exe" -B (Join-Path $PSScriptRoot $TestScript) --port $IpcPort --profile $profile
    if($LASTEXITCODE -ne 0){throw "$TestScript verification failed: $profile"}
    [IO.File]::WriteAllText("$profile/g81-test-stop",'Stop this isolated server')
    if(!$process.WaitForExit(20000) -or $process.ExitCode -ne 0){throw 'Isolated server did not unload cleanly'}
    $report=@{profile=$profile;packageSHA256=(Get-FileHash -LiteralPath $package).Hash;processId=$process.Id;exitCode=$process.ExitCode;mode='isolated dedicated native server; no clients or model requests'}
    $report | ConvertTo-Json | Set-Content "$profile/manifest.json"
    Write-Output "$TestScript native passed: $profile"
} finally {
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
