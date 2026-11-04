$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'server_ai_native_evidence.ps1')
$started=[datetimeoffset]::Parse('2026-10-26T12:00:00Z').UtcDateTime
$exited=$started.AddMinutes(1)
function Event($name,$processId=42,$world='this-world',$utc='2026-10-26T12:00:10Z'){
    $value=@{probe='VirtualPlayerEngineProbe';role='native-server';eventName=$name;process=$processId;WorldSession=$world;utc=$utc;programIsMainThread=$true;fault=$null}
    if($name -eq 'spawn'){$value.actor=@{sameReference=$true;active=$true}}else{
        $value.Phase='released; fresh server process required to re-arm';$value.actorSlot=-1;$value.actor=$null;$value.departures=1;$value.detail=@{same=$true;occupied=$false}
    }
    'prefix '+($value|ConvertTo-Json -Compress -Depth 8)
}
$spawn=Event 'spawn'
$ownTeardown=Event 'teardown' -utc '2026-10-26T12:00:20Z'
$foreign=@((Event 'teardown' -processId 99),(Event 'teardown' -world 'previous-world'),(Event 'teardown' -utc '2026-10-26T11:59:59Z'),(Event 'teardown' -utc '2026-10-26T12:01:01Z'),'unrelated log line','broken {json')
$filtered=@(Get-ServerAiRunEvents -Lines (@($spawn)+$foreign) -ProcessId 42 -WorldSession 'this-world' -StartedUtc $started -ExitedUtc $exited)
if($filtered.Count -ne 1){throw 'Foreign/time-window/malformed logs were not excluded'}
$rejected=$false
try {$null=Assert-ServerAiRunTeardown -Events $filtered}catch {$rejected=$true}
if(!$rejected){throw 'Historical/foreign teardown falsely passed evidence checks'}
Write-Output 'PASS native evidence: foreign process, old world, PID reuse outside lifetime and malformed logs cannot satisfy teardown'
$filtered=@(Get-ServerAiRunEvents -Lines (@($spawn,$ownTeardown)+$foreign) -ProcessId 42 -WorldSession 'this-world' -StartedUtc $started -ExitedUtc $exited)
$evidence=Assert-ServerAiRunTeardown -Events $filtered
if($filtered.Count -ne 2 -or $evidence.teardown.process -ne 42){throw 'Current process evidence was lost'}
Write-Output 'PASS native evidence: current process/world spawn and safe teardown accepted'
foreach($property in @('actorSlot','Phase','departures','actor','detail','fault')){
    $bad=($ownTeardown.Substring($ownTeardown.IndexOf('{'))|ConvertFrom-Json)
    switch($property){
        'actorSlot' {$bad.actorSlot=254}
        'Phase' {$bad.Phase='quarantined'}
        'departures' {$bad.departures=0}
        'actor' {$bad.actor=@{active=$true}}
        'detail' {$bad.detail.same=$false}
        'fault' {$bad.fault='quarantined'}
    }
    $rejected=$false
    try {$null=Assert-ServerAiRunTeardown -Events @($filtered[0],$bad)}catch {$rejected=$true}
    if(!$rejected){throw "Unsafe teardown accepted: $property"}
}
Write-Output 'PASS native evidence: incomplete actor cleanup, unsafe ownership, quarantine and missing release rejected (6 cases)'
