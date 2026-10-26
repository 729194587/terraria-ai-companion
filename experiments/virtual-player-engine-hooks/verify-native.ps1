param(
    [string]$TModLoaderPath='D:/steam/steamapps/common/tModLoader',
    [string]$WorldPath,
    [ValidateSet('All','Persistence','Survival','ItemUse','ItemUseFailure','Mining','MiningFailure','PickupOwnership')]
    [string]$Suite='All'
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'probe-paths.ps1')
$paths=Get-ProbePaths
if(!(Test-Path -LiteralPath $paths.Package)){throw 'Run build.ps1 and verify.ps1 first.'}
New-Item -ItemType Directory -Force -Path $paths.Validation|Out-Null
$cases=@(
    @{Name='Persistence';Script='verify-native-persistence.ps1';Port=27831},
    @{Name='Survival';Script='verify-native-survival.ps1';Port=27833},
    @{Name='ItemUse';Script='verify-native-item-use.ps1';Port=27834},
    @{Name='ItemUseFailure';Script='verify-native-item-use.ps1';Port=27835;Failure=$true},
    @{Name='Mining';Script='verify-native-mining.ps1';Port=27837},
    @{Name='MiningFailure';Script='verify-native-mining.ps1';Port=27838;Failure=$true},
    @{Name='PickupOwnership';Script='verify-native-pickup-ownership.ps1';Port=27836}
)
$results=@()
foreach($case in $cases|Where-Object {$Suite -eq 'All' -or $_.Name -eq $Suite}) {
    $arguments=@{TModLoaderPath=$TModLoaderPath;Port=$case.Port}
    if($WorldPath){$arguments.WorldPath=$WorldPath}
    if($case.Failure){$arguments.Failure=$true}
    $log=Join-Path $paths.Validation ('native-'+$case.Name+'.log')
    Write-Output ('Running isolated native suite: '+$case.Name)
    & (Join-Path $PSScriptRoot $case.Script) @arguments | Tee-Object -FilePath $log
    if(!$?){throw ('Native suite failed: '+$case.Name)}
    $results+=@{suite=$case.Name;status='passed';log=$log;forcedTerminationExpected=[bool]$case.Failure}
}
$report=@{version=$paths.Version;packageSHA256=(Get-FileHash -LiteralPath $paths.Package -Algorithm SHA256).Hash;
    suites=$results;hostAndPlay='not executed';remoteMultiplayer='not executed'}
$report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $paths.Validation ('native-'+$Suite+'-summary.json'))
Write-Output ('Native suites passed: '+$results.Count+'; graphical Host & Play / remote multiplayer NOT EXECUTED.')
