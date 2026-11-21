# Resolve shared inputs without sharing build outputs or writable test profiles.
function Get-NativeTestInputRoots {
    param([string]$Repo)
    Join-Path $Repo '.local'
    if (!(Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) { return }
    $worktrees = @(& git -C $Repo worktree list --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0 -or !$worktrees.Count -or $worktrees[0] -notmatch '^worktree (.+)$') { return }
    $primary = $Matches[1]
    if ($primary -ne $Repo.Replace('\','/')) { Join-Path $primary '.local' }
}

function Resolve-NativeTestInput {
    param(
        [string]$Repo,
        [string]$ExplicitPath,
        [ValidateSet('Python','World','CharacterWorld')][string]$Kind
    )
    if ($ExplicitPath) {
        $item = Get-Item -LiteralPath $ExplicitPath -ErrorAction Stop
        if ($item -isnot [IO.FileInfo]) { throw "Expected a file for $Kind input: $ExplicitPath" }
        return $item.FullName
    }
    $roots = @(Get-NativeTestInputRoots -Repo $Repo)
    $paths = switch ($Kind) {
        Python { 'lua-venv/Scripts/python.exe' }
        World { 'native-test-fixtures/gameplay/test.wld'; 'virtual-player-control-host/Worlds/test.wld' }
        CharacterWorld { 'native-test-fixtures/world-character/test.wld' }
    }
    foreach ($relative in $paths) {
        foreach ($root in $roots) {
            $candidate = Join-Path $root $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    $parameter = if ($Kind -eq 'Python') { '-PythonExecutable' } else { '-WorldPath' }
    throw "Missing native test input ($Kind). Pass $parameter or provide $($paths[0]) under $($roots -join ' or ')."
}
