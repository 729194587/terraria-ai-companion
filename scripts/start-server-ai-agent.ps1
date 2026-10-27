param([int]$Port=18777,[int]$AiPort=18779,[string]$PythonExecutable)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if($Port -lt 1 -or $Port -gt 65535 -or $AiPort -lt 1 -or $AiPort -gt 65535 -or $Port -eq $AiPort){throw 'Choose two distinct valid bridge ports.'}
if(!$PythonExecutable){$PythonExecutable=Join-Path $repo '.local/lua-venv/Scripts/python.exe'}
if(!(Test-Path -LiteralPath $PythonExecutable)){throw 'Python environment missing. Create .local/lua-venv and install python/requirements.txt first.'}
Write-Output 'Enter Host & Play with CompanionBridge 0.1.48 first. Local chat: !ai explore nearby; avoid danger and stop safely'
Write-Output 'Stop or replace exploration: send a new !ai instruction, or Ctrl+C here and wait for cleanup.'
Push-Location $repo
try {
    & $PythonExecutable (Join-Path $repo 'python/agent.py') --server-ai --port $Port --ai-port $AiPort --timeout 5
    if($LASTEXITCODE -ne 0){throw "Agent exited with code $LASTEXITCODE"}
}finally{Pop-Location}
