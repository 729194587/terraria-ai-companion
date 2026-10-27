param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[string]$PythonExecutable)
$arguments=@{TModLoaderPath=$TModLoaderPath;Port=27861;IpcPort=27862;Atomic=$true;Reads=$true;Actions=$true;
    PackagePath=(Join-Path (Split-Path $PSScriptRoot -Parent) '.local/companion-g82-build/Mods/CompanionBridge.tmod')}
if($WorldPath){$arguments.WorldPath=$WorldPath}
if($PythonExecutable){$arguments.PythonExecutable=$PythonExecutable}
& (Join-Path $PSScriptRoot 'verify_server_ai_native.ps1') @arguments
if(!$?){throw 'Native Lua server action verification failed'}
