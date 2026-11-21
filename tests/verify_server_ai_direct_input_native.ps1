param([string]$TModLoaderPath,[string]$EngineProfile, [string]$WorldPath,[string]$CharacterSeed, [string]$PackagePath, [int]$Port=27941, [int]$IpcPort=27942, [string]$TestScript='native_direct_input.py', [switch]$Scene, [switch]$Equipment, [switch]$Torch, [switch]$Combat,[string]$SamplePackage,[string]$PythonExecutable,
    [ValidateSet('eater','destroyers','twins','two-twins','moon')] [string]$BossProbe, [switch]$KeepProfile)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/engine-profile.ps1')
$selectedEngine=Resolve-CompanionEngine -Name $EngineProfile -Path $TModLoaderPath
$TModLoaderPath=$selectedEngine.Path
if(!$PackagePath){$PackagePath=$selectedEngine.Package}
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'scripts/native-test-inputs.ps1')
if($Port -eq $IpcPort -or @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {$_.Port -in @($Port,$IpcPort)}).Count){throw 'Test ports must be distinct and unused'}
$world=Resolve-NativeTestInput -Repo $repo -Kind World -ExplicitPath $WorldPath
$PythonExecutable=Resolve-NativeTestInput -Repo $repo -Kind Python -ExplicitPath $PythonExecutable
$package=(Resolve-Path -LiteralPath $PackagePath).Path
Assert-CompanionPackage -Package $package -Engine $selectedEngine
$profile=Join-Path $repo ('.local/direct-input-native-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods","$profile/Worlds" | Out-Null
if($CharacterSeed){Copy-Item -LiteralPath (Resolve-Path $CharacterSeed).Path -Destination "$profile/VirtualPlayerEngineProbe" -Recurse}
Copy-Item -LiteralPath $world -Destination "$profile/Worlds/test.wld"
$twld=[IO.Path]::ChangeExtension($world,'.twld')
if(Test-Path -LiteralPath $twld){Copy-Item -LiteralPath $twld -Destination "$profile/Worlds/test.twld"}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
if($SamplePackage){
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $SamplePackage).Path -Destination "$profile/Mods/BossStrategySample.tmod"
    [IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge","BossStrategySample"]')
}else{[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')}
[IO.File]::WriteAllText("$profile/isolated-action-fixture",'Explicit copied-world diagnostic only')
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $TModLoaderPath 'dotnet/dotnet.exe'))
$info.WorkingDirectory=$TModLoaderPath;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
foreach($arg in @('tModLoader.dll','-server','-world',"$profile/Worlds/test.wld",'-port',"$Port",'-tmlsavedirectory',$profile,'-modpath',"$profile/Mods",'-companionserverai','-companionengineprofile',$selectedEngine.Name,'-companionaiport',"$IpcPort",'-companionaiipctest','-companionactionstest')){$info.ArgumentList.Add($arg)}
if($SamplePackage){
    $info.ArgumentList.Add('-companionbosssamplehash')
    $info.ArgumentList.Add((Get-FileHash -LiteralPath "$profile/Mods/BossStrategySample.tmod").Hash)
}
if($BossProbe){$info.ArgumentList.Add('-companionbossprobe');$info.ArgumentList.Add($BossProbe);$TestScript='native_boss_probe.py'}
elseif($Combat){$info.ArgumentList.Add('-companioncombattest');$TestScript='native_combat.py'}
elseif($Torch){$info.ArgumentList.Add('-companiontorchtest');$TestScript='native_auto_torch.py'}
elseif($Equipment){$info.ArgumentList.Add('-companionequipmenttest');$TestScript='native_equipment.py'}
elseif($Scene){$info.ArgumentList.Add('-companionscenetest');$info.ArgumentList.Add('-companionperceptiontest');$info.ArgumentList.Add('-companionscenelighttest')}
else{$info.ArgumentList.Add('-companiondirectinputtest')}
$process=[Diagnostics.Process]::Start($info)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try {
    & $PythonExecutable -B (Join-Path $PSScriptRoot $TestScript) --port $IpcPort --profile $profile
    if($LASTEXITCODE -ne 0){throw "$TestScript verification failed: $profile"}
    [IO.File]::WriteAllText("$profile/g81-test-stop",'Stop this isolated server')
    if(!$process.WaitForExit(20000) -or $process.ExitCode -ne 0){throw 'Isolated server did not unload cleanly'}
    $report=@{profile=$profile;packageSHA256=(Get-FileHash -LiteralPath "$profile/Mods/CompanionBridge.tmod").Hash;processId=$process.Id;exitCode=$process.ExitCode;mode='isolated dedicated native server; no clients or model requests'}
    if($SamplePackage){$report.sampleSHA256=(Get-FileHash -LiteralPath "$profile/Mods/BossStrategySample.tmod").Hash}
    $report | ConvertTo-Json | Set-Content "$profile/manifest.json"
    Write-Output "$TestScript native passed: $profile"
} finally {
    if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
    [IO.File]::WriteAllText("$profile/stdout.log",$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText("$profile/stderr.log",$stderr.GetAwaiter().GetResult())
    $process.Dispose()
    if (!$KeepProfile) {
        try {
            $profileItem = Get-Item -LiteralPath $profile -Force
            $localRoot = [IO.Path]::GetFullPath((Join-Path $repo '.local'))
            if ($profileItem.Parent.FullName -ne $localRoot -or
                $profileItem.Name -cnotmatch '^direct-input-native-[a-f0-9]{32}$') {
                throw "Unexpected native test profile path: $profile"
            }
            for ($ancestor = $profileItem; $ancestor; $ancestor = $ancestor.Parent) {
                if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Profile path contains a reparse point: $($ancestor.FullName)"
                }
            }
            foreach ($name in 'Worlds', 'VirtualPlayerEngineProbe', 'Players', 'Mods') {
                $path = Join-Path $profileItem.FullName $name
                if (!(Test-Path -LiteralPath $path)) { continue }
                $item = Get-Item -LiteralPath $path -Force
                if ($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "Expected an ordinary test directory: $path"
                }
                $link = Get-ChildItem -LiteralPath $path -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1
                if ($link) { throw "Test directory contains a reparse point: $($link.FullName)" }
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        } catch {
            Write-Warning "Could not clean up native test data in ${profile}: $_"
        }
    }
}
