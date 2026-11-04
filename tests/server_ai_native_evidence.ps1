function Get-ServerAiRunEvents {
    param([string[]]$Lines,[int]$ProcessId,[string]$WorldSession,[datetime]$StartedUtc,[datetime]$ExitedUtc)
    foreach($line in $Lines){
        $offset=$line.IndexOf('{')
        if($offset -lt 0){continue}
        try {
            $event=$line.Substring($offset)|ConvertFrom-Json -ErrorAction Stop
            $utc=([datetimeoffset]$event.utc).UtcDateTime
        } catch {continue}
        if($event.probe -eq 'VirtualPlayerEngineProbe' -and $event.role -eq 'native-server' `
            -and $event.process -eq $ProcessId -and $event.WorldSession -eq $WorldSession `
            -and $utc -ge $StartedUtc -and $utc -le $ExitedUtc){$event}
    }
}

function Assert-ServerAiRunTeardown {
    param([object[]]$Events)
    $spawn=@($Events|Where-Object {$_.eventName -eq 'spawn' -and $_.actor.sameReference -eq $true -and $_.actor.active -eq $true})
    $teardown=@($Events|Where-Object {
        $_.eventName -eq 'teardown' -and $_.Phase -eq 'released; fresh server process required to re-arm' `
        -and $_.actorSlot -eq -1 -and $null -eq $_.actor -and $_.departures -ge 1 `
        -and $_.detail.same -eq $true -and $_.detail.occupied -eq $false -and $null -eq $_.fault `
        -and $_.programIsMainThread -eq $true
    })
    if(!$spawn.Count -or !$teardown.Count){throw 'This process/world has no matching native spawn, actor cleanup and reservation-release evidence.'}
    $last=$teardown[-1]
    if([datetimeoffset]$last.utc -lt [datetimeoffset]$spawn[0].utc){throw 'Teardown predates this run spawn.'}
    @{spawn=$spawn[0];teardown=$last}
}
