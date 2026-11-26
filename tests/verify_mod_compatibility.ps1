param([string]$WorkshopRoot = 'D:/steam/steamapps/workshop/content/1281930',
      [string]$PackagePath, [string[]]$Cases = @('Vanilla','Calamity','RecipeBrowser','Quality','Combined'),
      [int]$Port = 28960, [switch]$Crafting, [ValidateSet('Actions','Restore','Combat','Equipment','Network','PotionBuffs')][string]$Scenario = 'Actions')
$ErrorActionPreference = 'Stop'
$mods = @{
    Calamity = @('2824688072/2026.6/CalamityMod.tmod','2824688266/2025.12/CalamityModMusic.tmod')
    RecipeBrowser = @('2619954303/2026.7/RecipeBrowser.tmod')
    Quality = @('2797518634/2026.3/ImproveGame.tmod','3403460014/2026.3/SilkyUIFramework.tmod')
    Vanilla = @()
}
$mods.Combined = $mods.Calamity + $mods.RecipeBrowser + $mods.Quality
$failed = @()
foreach ($case in $Cases) {
    if (!$mods.ContainsKey($case)) { throw "Unknown compatibility case $case" }
    $paths = @($mods[$case] | ForEach-Object { Join-Path $WorkshopRoot $_ })
    Write-Output "Testing $case"
    try {
        if ($Scenario -eq 'PotionBuffs') {
            & "$PSScriptRoot/verify_server_ai_direct_input_native.ps1" -TestScript native_potion_buffs.py -KeepProfile -Port $Port -IpcPort ($Port+1) -PackagePath $PackagePath -ModPackages $paths
            if ($case -eq 'Quality') {
                & "$PSScriptRoot/verify_server_ai_direct_input_native.ps1" -TestScript native_potion_buffs.py -KeepProfile -Port $Port -IpcPort ($Port+1) -PackagePath $PackagePath -ModPackages $paths -DisablePotionSharing
            }
            continue
        }
        if ($Scenario -eq 'Network') {
            & "$PSScriptRoot/verify_server_ai_native.ps1" -KeepProfile -ItemInfo -CompatibilityQueries -TcpCapacity -Port $Port -IpcPort ($Port+1) -PackagePath $PackagePath -ModPackages $paths
            continue
        }
        if ($Scenario -eq 'Restore') {
            & "$PSScriptRoot/verify_world_character_native.ps1" -RestoreOnly -Port $Port -PackagePath $PackagePath -ModPackages $paths
            continue
        }
        if ($Scenario -in 'Combat','Equipment') {
            $extra = @{}
            if ($Scenario -eq 'Equipment') { $extra.TestScript = 'native_mod_equipment.py' }
            & "$PSScriptRoot/verify_server_ai_direct_input_native.ps1" @extra -Combat:($Scenario -eq 'Combat') -Equipment:($Scenario -eq 'Equipment') -KeepProfile -Port $Port -IpcPort ($Port+1) -PackagePath $PackagePath -ModPackages $paths
            continue
        }
        & "$PSScriptRoot/verify_server_ai_native.ps1" -ModPackages $paths -PackagePath $PackagePath -Atomic -Reads -ItemInfo -CompatibilityQueries -Actions -Perception -Crafting:$Crafting -Port $Port -IpcPort ($Port+1)
    } catch {
        $failed += $case
        Write-Warning "$case failed at $($_.InvocationInfo.PositionMessage) $($_.Exception.Message)"
    }
}
if ($failed.Count) { throw "Failed compatibility cases: $($failed -join ', ')" }
