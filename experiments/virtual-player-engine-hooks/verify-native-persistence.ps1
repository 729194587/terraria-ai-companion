param(
    [string]$TModLoaderPath = 'D:/steam/steamapps/common/tModLoader',
    [string]$WorldPath,
    [int]$Port = 27831,
    [string]$PackagePath,
    [ValidateSet('VirtualPlayerEngineProbe','CompanionBridge')][string]$ModName = 'VirtualPlayerEngineProbe',
    [switch]$StarterInventory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
$repo=$paths.Repository
if (!$WorldPath) { $WorldPath = Join-Path $repo '.local/virtual-player-network-host/Worlds/test.wld' }
$WorldPath = (Resolve-Path -LiteralPath $WorldPath).Path
$package = if ($PackagePath) { (Resolve-Path -LiteralPath $PackagePath).Path } else { $paths.Package }
if (!(Test-Path -LiteralPath $package)) { throw 'Build the current probe first.' }
if (@(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count) { throw "Diagnostic port $Port is in use." }
$ipcPort = $Port + 1
if ($ModName -eq 'CompanionBridge' -and @(Get-NetTCPConnection -LocalPort $ipcPort -State Listen -ErrorAction SilentlyContinue).Count) { throw "Diagnostic IPC port $ipcPort is in use." }
$profile = Join-Path $repo ('.local/character-native-validation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$profile/Mods", "$profile/Worlds" | Out-Null
$world = Join-Path $profile 'Worlds/test.wld'
Copy-Item -LiteralPath $WorldPath -Destination $world
$modWorld = [IO.Path]::ChangeExtension($WorldPath, '.twld')
if (Test-Path -LiteralPath $modWorld) { Copy-Item -LiteralPath $modWorld -Destination ([IO.Path]::ChangeExtension($world, '.twld')) }
Copy-Item -LiteralPath $package -Destination "$profile/Mods/$ModName.tmod"
Set-Content -LiteralPath "$profile/Mods/enabled.json" -Value ('["' + $ModName + '"]')
$resultPath = Join-Path $profile 'g31-native-result.json'
$id = $null
foreach ($round in 1,2) {
    $arguments = @('"tModLoader.dll"', '-server', '-world', ('"' + $world + '"'), '-port', "$Port",
        '-tmlsavedirectory', ('"' + $profile + '"'), '-modpath', ('"' + $profile + '/Mods"'), '-g31selftest')
    if ($round -eq 2) { $arguments += @('-g31loadid', $id) }
    if ($StarterInventory) { $arguments += '-r1startertest' }
    if ($ModName -eq 'CompanionBridge') { $arguments += @('-companionserverai', '-companionaiport', "$ipcPort") }
    $process = Start-Process -FilePath (Join-Path $TModLoaderPath 'dotnet/dotnet.exe') -ArgumentList $arguments `
        -WorkingDirectory $TModLoaderPath -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$profile/native-$round.stdout.log" -RedirectStandardError "$profile/native-$round.stderr.log"
    try {
        $null=$process.Handle
        if (!$process.WaitForExit(90000)) { throw 'Isolated native validation timed out.' }
        $process.WaitForExit();$process.Refresh()
        if ($process.ExitCode -ne 0) { throw "Native diagnostic process failed: $($process.ExitCode)" }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.result -ne 'native-persistence-selftest-passed' -or !$result.corruptLoadRejected `
            -or ($StarterInventory -and (!$result.starterInventory -or !$result.repeatedSpawnPreserved)) `
            -or ($round -eq 2 -and !$result.restoredFromPriorProcess)) { throw "Native validation failed: $(Get-Content $resultPath -Raw)" }
        $id = $result.id
        Move-Item -LiteralPath $resultPath -Destination "$profile/process-$round-result.json"
    } finally {
        # This process was created above for a new private diagnostic profile.
        # Never enumerate or terminate unrelated tModLoader/Host & Play processes.
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
Write-Output "Native isolated persistence passed, including process restart: $profile"
Write-Output 'Host & Play: NOT EXECUTED. Remote multiplayer: NOT EXECUTED.'
