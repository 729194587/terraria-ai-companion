# Shared by build, launch and native verification entry points.
function Resolve-CompanionEngine {
    param([string]$Name,[string]$Path)
    $repo=Split-Path $PSScriptRoot -Parent
    $catalog=Get-Content -LiteralPath (Join-Path $repo 'CompanionBridge/EngineProfiles.json') -Raw | ConvertFrom-Json
    if(!$Name){$Name=$catalog.default}
    $entry=$catalog.profiles.PSObject.Properties[$Name]
    if(!$entry){throw "Unknown CompanionBridge engine profile: $Name"}
    $profile=$entry.Value
    if(!$Path){
        $Path=$profile.localPath
        if(![IO.Path]::IsPathRooted($Path)){$Path=Join-Path $repo $Path}
    }
    $Path=(Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
    $dll=Join-Path $Path 'tModLoader.dll'
    $hooks=Join-Path $Path 'Libraries/TerrariaHooks/0.0.0.0/TerrariaHooks.dll'
    if((Get-FileHash -LiteralPath $dll -ErrorAction Stop).Hash -cne $profile.engineHash -or
        (Get-FileHash -LiteralPath $hooks -ErrorAction Stop).Hash -cne $profile.hooksHash){
        throw "Engine or hooks fingerprint mismatch for $Name at $Path. Select the matching installation."
    }
    $output=Join-Path $repo ".local/companion-build-$Name"
    [pscustomobject]@{Name=$Name;Path=$Path;Tml=$profile.tml;EngineHash=$profile.engineHash;HooksHash=$profile.hooksHash;
        Output=$output;Package=(Join-Path $output 'Mods/CompanionBridge.tmod')}
}

function Assert-CompanionPackage {
    param([string]$Package,[object]$Engine)
    $stream=[IO.File]::OpenRead($Package)
    try {
        $reader=[IO.BinaryReader]::new($stream)
        if([Text.Encoding]::ASCII.GetString($reader.ReadBytes(4)) -cne 'TMOD' -or $reader.ReadString() -cne $Engine.Tml){
            throw "Package was not built for $($Engine.Name): $Package"
        }
    } finally {$stream.Dispose()}
}
