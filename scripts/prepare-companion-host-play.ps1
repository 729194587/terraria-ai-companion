param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$SeedProfile,[int]$AiPort=18779)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$package=Join-Path $repo '.local/companion-g82-build/Mods/CompanionBridge.tmod'
if(!(Test-Path -LiteralPath $package)){throw 'Run scripts/build-companion.ps1 first.'}
if($AiPort -lt 1024 -or $AiPort -gt 65535 -or $AiPort -eq 18777 -or $AiPort -eq 7777){throw 'Choose a distinct AI IPC port.'}
# Always create a new profile; older experiments and formal saves stay intact.
$profile=Join-Path $repo ('.local/companion-g82-host-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path "$profile/Mods" | Out-Null
if($SeedProfile){
    $seed=(Resolve-Path -LiteralPath $SeedProfile).Path
    foreach($entry in @('Players','Worlds','config.json','input profiles.json','favorites.json')){
        $path=Join-Path $seed $entry
        if(Test-Path -LiteralPath $path){Copy-Item -LiteralPath $path -Destination $profile -Recurse}
    }
}
Copy-Item -LiteralPath $package -Destination "$profile/Mods/CompanionBridge.tmod"
[IO.File]::WriteAllText("$profile/Mods/enabled.json",'["CompanionBridge"]')
$hash=(Get-FileHash -LiteralPath $package).Hash
$launcher=@'
param([string]$TModLoaderPath='__TML__')
$ErrorActionPreference='Stop'
$package=Join-Path $PSScriptRoot 'Mods/CompanionBridge.tmod'
if((Get-FileHash -LiteralPath $package).Hash -ne '__HASH__'){throw 'Package changed; prepare a new isolated profile.'}
$enabled=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Mods/enabled.json') -Raw | ConvertFrom-Json
if(@($enabled|Where-Object {$_ -notin @('CompanionBridge','ModLoader')}).Count){throw 'G8.2 requires CompanionBridge and tModLoader only.'}
foreach($port in @(7777,18777,__PORT__)){
    if(@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count){throw "Port $port is occupied; close the previous test before launch."}
}
$previousEnabled=$env:COMPANION_SERVER_AI
$previousPort=$env:COMPANION_AI_PORT
$env:COMPANION_SERVER_AI='1'
$env:COMPANION_AI_PORT='__PORT__'
Push-Location $TModLoaderPath
try { & (Join-Path $TModLoaderPath 'start-tModLoader.bat') -tmlsavedirectory $PSScriptRoot -modpath (Join-Path $PSScriptRoot 'Mods') }
finally { Pop-Location; $env:COMPANION_SERVER_AI=$previousEnabled; $env:COMPANION_AI_PORT=$previousPort }
'@
$launcher=$launcher.Replace('__TML__',$TModLoaderPath.Replace("'","''")).Replace('__HASH__',$hash).Replace('__PORT__',"$AiPort")
[IO.File]::WriteAllText("$profile/start-host-play.ps1",$launcher,[Text.UTF8Encoding]::new($true))
Write-Output "Prepared only; graphical Host & Play NOT RUN: $profile/start-host-play.ps1"
Write-Output "After manually selecting Multiplayer > Host & Play, run: python scripts/ai_player_state.py --port $AiPort"
Write-Output "Explicit Lua selection: & .local/lua-venv/Scripts/python.exe scripts/server_ai_select.py --port $AiPort --key 2"
Write-Output "Lua state/inventory reads (read-only by default): & .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py --port $AiPort"
Write-Output "Both G8.2 stages in one Lua run: & .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py --port $AiPort --key 2"
Write-Output "Explicit Lua movement/use/mining: scripts/server_ai_actions.py --help; see docs/SERVER_AI_ACTIONS.md. No test terrain/items are created by this profile."
Write-Output "G8.4 Agent: & .local/lua-venv/Scripts/python.exe python/agent.py --server-ai --ai-port $AiPort ; then type !ai <task> in local game chat. Model settings reuse .env."
