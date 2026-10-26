param([string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',[string]$WorldPath,[string]$PythonExecutable)
$arguments=@{TModLoaderPath=$TModLoaderPath;Port=27851;IpcPort=27852;Atomic=$true;PackagePath=(Join-Path (Split-Path $PSScriptRoot -Parent) '.local/companion-g82-build/Mods/CompanionBridge.tmod')}
if($WorldPath){$arguments.WorldPath=$WorldPath}
if($PythonExecutable){$arguments.PythonExecutable=$PythonExecutable}
& (Join-Path $PSScriptRoot 'verify_server_ai_native.ps1') @arguments
if(!$?){throw 'G8.2 native atomic validation failed'}
