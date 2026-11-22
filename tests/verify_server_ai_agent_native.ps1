param([string]$TModLoaderPath,[string]$EngineProfile,[string]$PackagePath,[string]$WorldPath,[string]$PythonExecutable,[switch]$LiveModel)
$arguments=@{TModLoaderPath=$TModLoaderPath;Port=27871;IpcPort=27872;Atomic=$true;Reads=$true;Actions=$true;Agent=$true;LiveModel=$LiveModel;
    EngineProfile=$EngineProfile;PackagePath=$PackagePath}
if($WorldPath){$arguments.WorldPath=$WorldPath}
if($PythonExecutable){$arguments.PythonExecutable=$PythonExecutable}
& (Join-Path $PSScriptRoot 'verify_server_ai_native.ps1') @arguments
if(!$?){throw 'Native server AI Agent integration verification failed'}
