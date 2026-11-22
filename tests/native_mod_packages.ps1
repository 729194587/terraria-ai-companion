function Set-NativeModPackages {
    param([string]$ProfilePath, [string[]]$Packages)
    $enabled = @('CompanionBridge')
    foreach ($path in $Packages) {
        Copy-Item -ErrorAction Stop -LiteralPath $path -Destination "$ProfilePath/Mods"
        $enabled += [IO.Path]::GetFileNameWithoutExtension($path)
    }
    [IO.File]::WriteAllText("$ProfilePath/Mods/enabled.json", (ConvertTo-Json -InputObject $enabled -Compress))
    $enabled
}
