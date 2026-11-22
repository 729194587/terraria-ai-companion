param([string]$TModLoaderPath,[string]$EngineProfile,[string]$PackagePath,[string]$WorldPath,[string]$PythonExecutable,[switch]$Reads)
$arguments=@{TModLoaderPath=$TModLoaderPath;Port=27851;IpcPort=27852;Atomic=$true;EngineProfile=$EngineProfile;PackagePath=$PackagePath}
if($WorldPath){$arguments.WorldPath=$WorldPath}
if($PythonExecutable){$arguments.PythonExecutable=$PythonExecutable}
if($Reads){$arguments.Reads=$true}
& (Join-Path $PSScriptRoot 'verify_server_ai_native.ps1') @arguments
if(!$?){throw 'G8.2 native atomic validation failed'}
