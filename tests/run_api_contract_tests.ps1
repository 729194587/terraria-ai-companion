param([string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sources=@('CompanionBridge/AtomicParameters.cs','CompanionBridge/SceneLayers.cs','tests/AtomicParameterContractTests.cs') | ForEach-Object {Join-Path $repo $_}
$references=Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies $references
$result=[AtomicParameterContractTests]::Run((Join-Path $PSScriptRoot 'fixtures/api-contract-vectors.json'))
if($OutputPath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$result,[Text.UTF8Encoding]::new($false))}
Write-Output ('API contract vectors passed: '+($result|ConvertFrom-Json).Count)
