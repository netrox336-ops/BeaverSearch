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
$local = '.\src\BeaverSearch\Services\LocalStore.cs'
$cache = '.\src\BeaverSearch\Models\CacheModels.cs'
$dom = '.\src\BeaverSearch\Services\RenderedDomLoader.cs'
$vm = '.\src\BeaverSearch\ViewModels\MainViewModel.cs'

Need $market 'steamcommunity\.com/market/priceoverview' 'Steam Community Market fallback endpoint marker missing.'
Need $market 'currency=5' 'Steam Market RUB fallback currency marker missing.'
Need $market 'PositiveTtl\s*=\s*TimeSpan\.FromMinutes\(10\)' 'Steam Market positive cache is not 10 minutes.'
Need $bulk 'api\.skinport\.com/v1/items' 'Lazy bulk price catalog endpoint marker missing.'
Need $bulk 'CatalogTtl\s*=\s*TimeSpan\.FromMinutes\(20\)' 'Bulk catalog cache is not 20 minutes.'
Need $bulk '_catalogDownloadGate\s*=\s*new\(1, 1\)' 'Bulk catalog downloads are not serialized.'
Need $bulk 'MaxSteamFallbackNames\s*=\s*16' 'Bounded Steam fallback limit is missing.'
Need $valuation '_playerValuationGate\s*=\s*new\(4, 4\)' 'Heavy player valuation gate is not 4.'
Need $valuation 'TemporaryFailure' 'Transient Steam inventory failure protection marker missing.'
Need $valuation 'PricingAvailable' 'False-zero price protection marker missing.'
Need $inventory 'PageSize\s*=\s*2000' 'Steam inventory page size must be 2000; count=5000 can return HTTP 403 for public inventories.'
Need $inventory '_inventoryRequestGate\s*=\s*new\(2, 2\)' 'Steam inventory request gate is not 2.'
Need $inventory 'TimeSpan\.FromMilliseconds\(450\)' 'Steam inventory request pacing marker missing.'
Need $inventory 'TransientFailure' 'Steam inventory transient/private classification marker missing.'
Need $inventory 'ConfigureAwait\(false\)' 'Steam inventory off-dispatcher awaits missing.'
Need $cache 'PriceEngineVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*4' 'PriceEngineVersion 4 marker missing.'
Need $local 'CurrentPriceEngineVersion\s*=\s*4' 'LocalStore price-engine v4 migration marker missing.'
Need $local 'FlushCacheLoopAsync' 'Debounced cache writer marker missing.'
Need $dom 'CacheTtl\s*=\s*TimeSpan\.FromSeconds\(75\)' 'Browser probe cache TTL is not 75 seconds.'
Need $dom 'BrowserGate\s*=\s*new\(1, 1\)' 'Browser probe concurrency is not 1.'
Need $dom 'ProcessPriorityClass\.BelowNormal' 'Browser BelowNormal priority marker missing.'
Need $dom 'FragmentScript' 'Compact DOM fragment capture marker missing.'
Need $vm 'CommunityServerBatchSize\s*=\s*10' 'Monitoring server batch is not 10.'
Need $vm '_scanGate\s*=\s*new\(4, 4\)' 'Player scan gate is not 4.'
Need $vm 'WaitForCurrentBatchScansAsync' 'Sequential batch completion wait marker missing.'
Need $vm 'Task\.Run\(\(\)\s*=>\s*loader\(ct\)' 'Source parsing is not moved off the WPF dispatcher.'

if ($issues.Count -gt 0) {
    Write-Host 'BeaverSearch HOTFIX PREFLIGHT: FAIL'
    foreach ($i in $issues) { Write-Host "- $i" }
    exit 1
}

Write-Host 'BeaverSearch HOTFIX PREFLIGHT: PASS'
Write-Host '- monitoring: sequential batches of 10 servers: checked'
Write-Host '- source HTML/JSON parsing off WPF dispatcher: checked'
Write-Host '- Steam inventory: count=2000 / request gate=2 / paced / transient 403 protection: checked'
Write-Host '- pricing: 20m bulk RUB catalog + bounded Steam Market fallback: checked'
Write-Host '- false-zero cache migration v4: checked'
Write-Host '- valuation concurrency 4: checked'
Write-Host '- debounced cache writes: checked'
Write-Host '- browser probe: 1 process / 75s cache / compact DOM / BelowNormal: checked'
exit 0
