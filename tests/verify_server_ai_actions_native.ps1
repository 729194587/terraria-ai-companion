param([string]$TModLoaderPath,[string]$EngineProfile,[string]$PackagePath,[string]$WorldPath,[string]$PythonExecutable)
$arguments=@{TModLoaderPath=$TModLoaderPath;Port=27861;IpcPort=27862;Atomic=$true;Reads=$true;Actions=$true;
    EngineProfile=$EngineProfile;PackagePath=$PackagePath}
if($WorldPath){$arguments.WorldPath=$WorldPath}
if($PythonExecutable){$arguments.PythonExecutable=$PythonExecutable}
& (Join-Path $PSScriptRoot 'verify_server_ai_native.ps1') @arguments
if(!$?){throw 'Native Lua server action verification failed'}
