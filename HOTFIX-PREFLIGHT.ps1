$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$issues = New-Object System.Collections.Generic.List[string]
function Need([string]$path, [string]$pattern, [string]$message) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $script:issues.Add("Missing: $path") | Out-Null; return }
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ($text -notmatch $pattern) { $script:issues.Add($message) | Out-Null }
}

$market = '.\src\BeaverSearch\Services\SteamMarketPriceProvider.cs'
$bulk = '.\src\BeaverSearch\Services\SkinportPriceProvider.cs'
$valuation = '.\src\BeaverSearch\Services\InventoryValuationService.cs'
$inventory = '.\src\BeaverSearch\Services\SteamInventoryService.cs'
$presence = '.\src\BeaverSearch\Services\SteamInventoryPresenceService.cs'
$models = '.\src\BeaverSearch\Models\InventoryModels.cs'
$local = '.\src\BeaverSearch\Services\LocalStore.cs'
$cache = '.\src\BeaverSearch\Models\CacheModels.cs'
$dom = '.\src\BeaverSearch\Services\RenderedDomLoader.cs'
$vm = '.\src\BeaverSearch\ViewModels\MainViewModel.cs'

Need $market 'steamcommunity\.com/market/priceoverview' 'Steam Community Market fallback endpoint marker missing.'
Need $market 'currency=5' 'Steam Market RUB fallback currency marker missing.'
Need $market 'PositiveTtl\s*=\s*TimeSpan\.FromMinutes\(20\)' 'Steam Market positive cache is not 20 minutes.'
Need $market '_requestGate\s*=\s*new\(1, 1\)' 'Steam Market fallback requests are not serialized.'

Need $bulk 'api\.skincash\.gg/v1/prices' 'Primary no-key CS2 bulk price feed marker missing.'
Need $bulk 'market\.dota2\.net/api/v2/prices/class_instance/RUB\.json' 'Primary no-key Dota RUB feed marker missing.'
Need $bulk 'api\.skinport\.com/v1/items' 'Skinport secondary catalog endpoint marker missing.'
Need $bulk 'StringWithQualityHeaderValue\("br"\)' 'Skinport required Brotli Accept-Encoding marker missing.'
Need $bulk 'CatalogTtl\s*=\s*TimeSpan\.FromMinutes\(15\)' 'Bulk catalog cache is not 15 minutes.'
Need $bulk '_catalogDownloadGate\s*=\s*new\(1, 1\)' 'Bulk catalog downloads are not serialized.'
Need $bulk 'MaxSteamFallbackNames\s*=\s*20' 'Bounded Steam fallback limit is missing.'

Need $valuation '_playerValuationGate\s*=\s*new\(2, 2\)' 'Player valuation gate must be 2 in v6.'
Need $valuation 'SteamInventoryPresenceService' 'Steam app-presence precheck is not wired into valuation.'
Need $valuation 'IsKnownEmpty\(730\)' 'CS2 empty-inventory precheck marker missing.'
Need $valuation 'IsKnownEmpty\(570\)' 'Dota empty-inventory precheck marker missing.'
Need $valuation 'IsKnownEmpty\(252490\)' 'Rust empty-inventory precheck marker missing.'
Need $valuation 'DeferredGame' 'Partial valuation/deferred-game marker missing.'
Need $valuation 'HttpStatusCode\s*==\s*401' '401 empty/unallocated inventory compatibility guard missing.'
Need $models 'HasTemporaryFailures' 'Player partial-valuation state marker missing.'

Need $presence 'g_rgAppContextData' 'Steam profile inventory app-context marker missing.'
Need $presence 'PositiveTtl\s*=\s*TimeSpan\.FromMinutes\(20\)' 'Steam presence cache is not 20 minutes.'
Need $presence '_gate\s*=\s*new\(1, 1\)' 'Steam profile presence reads are not serialized.'
Need $presence 'IsKnownEmpty' 'Inventory presence empty-app helper missing.'

Need $inventory 'PageSize\s*=\s*2000' 'Steam inventory page size must be 2000 in v6.'
Need $inventory '_inventoryRequestGate\s*=\s*new\(1, 1\)' 'Steam inventory requests must be globally serialized.'
Need $inventory 'NormalSpacing\s*=\s*TimeSpan\.FromSeconds\(10\)' 'Steam inventory normal pacing must be 10 seconds.'
Need $inventory 'ThrottledSpacing\s*=\s*TimeSpan\.FromSeconds\(15\)' 'Steam inventory throttled pacing must be 15 seconds.'
Need $inventory 'response\.StatusCode\s*==\s*HttpStatusCode\.Unauthorized' 'Steam 401/null empty-app normalization marker missing.'
Need $inventory 'RegisterThrottleStrike' 'Adaptive 403/429 cooldown marker missing.'
Need $inventory 'CookieContainer' 'Anonymous Steam Community cookie jar marker missing.'
Need $inventory 'TransientFailure' 'Steam inventory transient/private classification marker missing.'
Need $inventory 'ConfigureAwait\(false\)' 'Steam inventory off-dispatcher awaits missing.'

$inventoryText = Get-Content -LiteralPath $inventory -Raw -Encoding UTF8
if ($inventoryText -match 'inventory/json/' -or $inventoryText -match 'GetLegacyInventoryAsync') {
    $issues.Add('Legacy inventory fallback is forbidden in v6 because it doubles requests after Steam throttle.') | Out-Null
}
if ($inventoryText -match 'attempt\s*<\s*2' -and $inventoryText -match '403|429') {
    $issues.Add('Immediate retry loop for Steam 403/429 may have returned.') | Out-Null
}

Need $cache 'PriceEngineVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*6' 'PriceEngineVersion 6 marker missing.'
Need $local 'CurrentPriceEngineVersion\s*=\s*6' 'LocalStore price-engine v6 migration marker missing.'
Need $local 'FlushCacheLoopAsync' 'Debounced cache writer marker missing.'

Need $dom 'CacheTtl\s*=\s*TimeSpan\.FromSeconds\(75\)' 'Browser probe cache TTL is not 75 seconds.'
Need $dom 'BrowserGate\s*=\s*new\(1, 1\)' 'Browser probe concurrency is not 1.'
Need $dom 'ProcessPriorityClass\.BelowNormal' 'Browser BelowNormal priority marker missing.'
Need $dom 'FragmentScript' 'Compact DOM fragment capture marker missing.'

Need $vm 'CommunityServerBatchSize\s*=\s*10' 'Monitoring server batch is not 10.'
Need $vm '_scanGate\s*=\s*new\(4, 4\)' 'Player scan gate is not 4.'
Need $vm 'Task\.WhenAll\(tasks\)\.WaitAsync\(ct\)' 'Monitoring batch does not wait for full completion.'
Need $vm 'HasTemporaryFailures' 'MainViewModel partial-valuation cache protection marker missing.'
Need $vm 'Task\.Run\(\(\)\s*=>\s*loader\(ct\)' 'Source parsing is not moved off the WPF dispatcher.'

$vmText = Get-Content -LiteralPath $vm -Raw -Encoding UTF8
if ($vmText -match 'WaitAsync\(TimeSpan\.FromSeconds\(90\)') {
    $issues.Add('Old 90-second batch timeout is still present; batches could overlap and recreate the backlog.') | Out-Null
}

if ($issues.Count -gt 0) {
    Write-Host 'BeaverSearch HOTFIX PREFLIGHT: FAIL'
    foreach ($i in $issues) { Write-Host "- $i" }
    exit 1
}

Write-Host 'BeaverSearch HOTFIX PREFLIGHT: PASS'
Write-Host '- monitoring: 10-server sequential batches / no overlap: checked'
Write-Host '- Steam profile app-presence precheck skips known-empty CS2/Dota/Rust inventories: checked'
Write-Host '- Steam inventory: modern endpoint only / count=2000 / one lane / 10s pacing / adaptive cooldown: checked'
Write-Host '- Steam inventory: 401+null is normalized as empty app inventory; legacy retry storm forbidden: checked'
Write-Host '- pricing: CS2 SkinCash + Dota public RUB feed + Skinport + Steam Market fallback: checked'
Write-Host '- partial valid valuations survive another game temporary failure: checked'
Write-Host '- false-zero cache migration v6: checked'
Write-Host '- browser probe: 1 process / 75s cache / compact DOM / BelowNormal: checked'
exit 0
