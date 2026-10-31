param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader', [string]$WorldPath='.local/virtual-player-control-host/Worlds/test.wld', [string]$PackagePath='.local/agent-build/Mods/CompanionBridge.tmod', [int]$Port=27941, [int]$IpcPort=27942)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if($Port -eq $IpcPort -or @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {$_.Port -in @($Port,$IpcPort)}).Count){throw 'Test ports must be distinct and unused'}
$world=(Resolve-Path -LiteralPath $WorldPath).Path
$package=(Resolve-Path -LiteralPath $PackagePath).Path
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
foreach($arg in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionaiport',"$IpcPort",'-companionaiipctest','-companionactionstest','-companiondirectinputtest')){$info.ArgumentList.Add($arg)}
$process=[Diagnostics.Process]::Start($info)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try {
    & "$repo/.local/lua-venv/Scripts/python.exe" -B "$PSScriptRoot/native_direct_input.py" --port $IpcPort --profile $profile
    if($LASTEXITCODE -ne 0){throw "Direct input verification failed: $profile"}
    [IO.File]::WriteAllText("$profile/g81-test-stop",'Stop this isolated server')
    if(!$process.WaitForExit(20000) -or $process.ExitCode -ne 0){throw 'Isolated server did not unload cleanly'}
    $report=@{profile=$profile;packageSHA256=(Get-FileHash -LiteralPath $package).Hash;processId=$process.Id;exitCode=$process.ExitCode;mode='isolated dedicated native server; no clients or model requests'}
    $report | ConvertTo-Json | Set-Content "$profile/manifest.json"
    Write-Output "Direct input native passed: $profile"
} finally {
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
