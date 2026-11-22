#Requires -Version 7.4
<#
.SYNOPSIS
Previews cleanup of isolated native test runs under the repository's .local.
.EXAMPLE
./scripts/cleanup-local.ps1
.EXAMPLE
./scripts/cleanup-local.ps1 -Directory 'container-native-xxxxxxxx' -Apply -WhatIf
.DESCRIPTION
Use exact immediate child names. Apply requires an explicit list; omit Apply to
preview. Keep protects additional runs for this invocation. The newest run of
each observed outcome and report type is always retained, including incomplete
runs. Archive any other evidence you need before cleanup. Run Apply with game
and native test runners stopped. Requires rg and Windows process inventory.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$Directory,
    [string[]]$Keep,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$families = [ordered]@{
    'container-native' = @('[a-z0-9_]{8}', 'container-results.json')
    'direct-input-native' = @('[a-f0-9]{32}', 'manifest.json')
    'placement-native-validation' = @('[a-f0-9]{32}', 'placement-native-result.json', 'item-use-native-result.json')
    'mining-native-validation' = @('[a-f0-9]{32}', 'mining-native-result.json')
    'navigation-recovery-native' = @('[a-f0-9]{32}', 'manifest.json')
}
if ($Apply -and !$Directory) { throw '-Apply requires exact names via -Directory.' }
$repo = (Get-Item -LiteralPath (Split-Path -Parent $PSScriptRoot)).FullName
$root = Join-Path $repo '.local'
if (!(Test-Path -LiteralPath $root) -and !$Directory) { return }

function Assert-PlainDirectory($item) {
    if ($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a real directory: $($item.FullName)"
    }
}

function Get-PlainTreeStats($path) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($path)
    $count = $bytes = 0L
    while ($pending.Count) {
        $node = Get-Item -LiteralPath $pending.Pop() -Force
        Assert-PlainDirectory $node
        foreach ($entry in Get-ChildItem -LiteralPath $node.FullName -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Tree contains a reparse point: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            else { $count++; $bytes += $entry.Length }
        }
    }
    @{ Count = $count; Bytes = $bytes }
}

function Get-EvidenceKind($item, $reports) {
    foreach ($report in $reports) {
        $path = Join-Path $item.FullName $report
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try {
            if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                return "$report/incomplete"
            }
            $data = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
            if ($report -eq 'manifest.json' -and $null -ne $data.exitCode) {
                if ($data.exitCode -eq 0) { return "$report/passed" }
                return "$report/failed"
            }
            if ($data.result -eq 'passed') { return "$report/passed" }
            if ($data.result -eq 'failed') { return "$report/failed" }
        } catch { return "$report/incomplete" }
        return "$report/incomplete"
    }
    'no-report/incomplete'
}

function Get-ProcessInventory {
    $snapshot = @(Get-CimInstance Win32_Process -Property Name, ProcessId, ExecutablePath, CommandLine)
    if ($PID -notin $snapshot.ProcessId) { throw 'Process inventory did not include this PowerShell process.' }
    $snapshot
}

function Get-ProcessBlock($name, $processes) {
    foreach ($process in $processes) {
        if ($process.ProcessId -eq $PID) { continue }
        $command = $process.CommandLine
        if (($command -and $command.IndexOf($name, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($process.ExecutablePath -and $process.ExecutablePath.IndexOf($name, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            return "Used by PID $($process.ProcessId)"
        }
        if ($process.Name -match 'terraria|tmodloader' -or
            $command -match 'tModLoader\.dll|verify_[^\s"''/\\]*native\.(?:py|ps1)' -or
            (!$command -and $process.Name -match '^(dotnet|pythonw?|pwsh|powershell|cmd)\.exe$')) {
            return "Game/test process or unavailable command line: $($process.Name) PID $($process.ProcessId)"
        }
    }
}

function Find-References($names) {
    $references = @{}
    if (!$names) { return $references }
    $pattern = ($names | ForEach-Object { [regex]::Escape($_) }) -join '|'
    $rg = (Get-Command rg -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $globs = foreach ($extension in 'ps1','psm1','psd1','cmd','bat','sh','py','js','cjs','mjs','ts','lua','cs','json','jsonc','toml','yaml','yml','ini','cfg','config','xml','md','txt','lnk') {
        '-g'; "*.$extension"
    }
    Push-Location -LiteralPath $repo
    try {
        $hits = @(& $rg --no-config --no-ignore --hidden --text --ignore-case --json -g '!.git' -g '!**/lua-venv/**' @globs -e $pattern -- .)
        if ($LASTEXITCODE -gt 1) { throw "rg reference scan exited with code $LASTEXITCODE." }
        foreach ($hit in $hits) {
            $event = ConvertFrom-Json -InputObject $hit
            if ($event.type -ne 'match') { continue }
            $path = $event.data.path.text
            if (!$path) { throw 'Reference scan returned an unreadable path.' }
            $path = [IO.Path]::GetFullPath($path, $repo)
            foreach ($match in $event.data.submatches) {
                $name = $match.match.text
                $inside = (Join-Path $root $name) + [IO.Path]::DirectorySeparatorChar
                if (!$path.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase) -and !$references.ContainsKey($name)) {
                    $references[$name] = [IO.Path]::GetRelativePath($repo, $path)
                }
            }
        }
    } finally { Pop-Location }
    $references
}

function Assert-LocalRoot {
    for ($ancestor = Get-Item -LiteralPath $root -Force; $ancestor; $ancestor = $ancestor.Parent) {
        Assert-PlainDirectory $ancestor
    }
}

Assert-LocalRoot
$runs = @{}
$representatives = @{}
foreach ($item in Get-ChildItem -LiteralPath $root -Directory -Force |
    Sort-Object LastWriteTimeUtc, Name -Descending) {
    foreach ($family in $families.Keys) {
        $suffix = $families[$family][0]
        if ($item.Name -cnotmatch ("\A" + [regex]::Escape($family) + "-$suffix\z")) { continue }
        $runs[$item.Name] = $item
        if (!($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            $reports = $families[$family] | Select-Object -Skip 1
            $kind = "$family/$(Get-EvidenceKind $item $reports)"
            if (!$representatives.ContainsKey($kind)) { $representatives[$kind] = $item.Name }
        }
        break
    }
}
foreach ($name in @($Directory) + @($Keep)) {
    if ($null -eq $name) { continue }
    if (!$name -or !$runs.ContainsKey($name)) { throw "Not an existing allowlisted directory: '$name'" }
}

$names = if ($Directory) { @($Directory | Sort-Object -Unique) } else { @($runs.Keys | Sort-Object) }
$checks = [Collections.Generic.List[string]]::new()
try { $processes = Get-ProcessInventory } catch { $checks.Add("Process inventory unavailable: $_") }
try { $refs = Find-References $names } catch { $checks.Add("Reference scan unavailable: $_") }
foreach ($check in $checks) { Write-Warning $check }

$plan = foreach ($name in $names) {
    $item = $runs[$name]
    $stats = @{ Count = $null; Bytes = $null }
    $reason = if ($name -in $Keep) { 'Explicitly retained' }
    elseif ($name -in $representatives.Values) { 'Representative evidence (newest of its outcome)' }
    elseif ($refs -and $refs.ContainsKey($name)) { "Referenced by $($refs[$name])" }
    elseif ($checks.Count) { 'Safety check unavailable' }
    else { Get-ProcessBlock $name $processes }
    try { $stats = Get-PlainTreeStats $item.FullName } catch { $reason = $_.Exception.Message }
    [pscustomobject]@{
        Directory = $item.Name
        Files = $stats.Count
        SizeBytes = $stats.Bytes
        ReclaimableBytes = if ($reason) { 0L } else { $stats.Bytes }
        Status = if ($reason) { $reason } else { 'Eligible' }
    }
}
$plan
if (!$Apply) { return }
if (@($plan | Where-Object Status -ne 'Eligible').Count) {
    throw 'At least one requested directory is blocked; nothing was deleted.'
}

foreach ($row in $plan) {
    $target = Join-Path $root $row.Directory
    if (!$PSCmdlet.ShouldProcess($target, 'Delete isolated test run recursively')) { continue }
    Assert-LocalRoot
    if (!(Split-Path -Parent ([IO.Path]::GetFullPath($target))).Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Target escaped .local: $target"
    }
    $refs = Find-References @($row.Directory)
    if ($refs.ContainsKey($row.Directory)) { throw "New reference found: $($refs[$row.Directory])" }
    $blocked = Get-ProcessBlock $row.Directory (Get-ProcessInventory)
    if ($blocked) { throw $blocked }
    $stats = Get-PlainTreeStats $target
    if ($stats.Count -ne $row.Files -or $stats.Bytes -ne $row.ReclaimableBytes) {
        throw "Contents changed after preview: $target"
    }
    $log = Join-Path $root ("cleanup-local-" + [guid]::NewGuid().ToString('N') + '.jsonl')
    $stream = [IO.File]::Open($log, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream)
    $record = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); directory = $row.Directory; files = $stats.Count; bytes = $stats.Bytes; status = 'started' }
    try {
        $writer.WriteLine(($record | ConvertTo-Json -Compress))
        $writer.Flush()
        $stream.Flush($true)
        try {
            Remove-Item -LiteralPath $target -Recurse -Force
            $record.status = 'deleted'
        } catch {
            $record.status = 'failed_or_partial'
            $record.error = $_.Exception.Message
            throw
        } finally {
            $record.utc = [DateTime]::UtcNow.ToString('o')
            $writer.WriteLine(($record | ConvertTo-Json -Compress))
            $writer.Flush()
            $stream.Flush($true)
        }
    } finally { $writer.Dispose() }
}
