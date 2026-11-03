param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',
    [string]$WorldPath, [string]$PackagePath, [int]$Port=27991, [int]$IpcPort=27992, [switch]$Baseline,[string[]]$Scenes)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$world=(Resolve-Path -LiteralPath $WorldPath).Path
$package=(Resolve-Path -LiteralPath $PackagePath).Path
$expected=[regex]::Match((Get-Content -LiteralPath (Join-Path $repo 'CompanionBridge/ServerPlayerRuntime/EngineHooks.cs') -Raw),
    'EngineHash = "([0-9A-F]+)"').Groups[1].Value
$actual=(Get-FileHash -LiteralPath (Join-Path $TModLoaderPath 'tModLoader.dll')).Hash
if(!$expected -or $actual -ne $expected){throw "Pinned native engine required; expected=$expected actual=$actual. No server started."}
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
    '-modpath',"$profile/Mods",'-companionserverai','-companionaiport',"$IpcPort",'-companionaiipctest',
    '-companionactionstest','-companionterraintest','-companionnavrecoverytest')){$info.ArgumentList.Add($arg)}
$process=[Diagnostics.Process]::Start($info)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try {
    $probeArgs=@('-B',(Join-Path $PSScriptRoot 'native_navigation_recovery.py'),'--port',"$IpcPort",'--profile',$profile)
    if($Baseline){$probeArgs+='--baseline'}
    if($Scenes){$probeArgs+=@('--scenes')+$Scenes}
    & "$repo/.local/lua-venv/Scripts/python.exe" @probeArgs
    if($LASTEXITCODE -ne 0){throw "Native recovery verification failed: $profile"}
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
