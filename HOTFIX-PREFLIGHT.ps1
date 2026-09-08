$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$issues = New-Object System.Collections.Generic.List[string]
function Need([string]$path, [string]$pattern, [string]$message) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $script:issues.Add("Missing: $path") | Out-Null; return }
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ($text -notmatch $pattern) { $script:issues.Add($message) | Out-Null }
}

$market = '.\src\BeaverSearch\Services\SteamMarketPriceProvider.cs'
$valuation = '.\src\BeaverSearch\Services\InventoryValuationService.cs'
$inventory = '.\src\BeaverSearch\Services\SteamInventoryService.cs'
$local = '.\src\BeaverSearch\Services\LocalStore.cs'
$cache = '.\src\BeaverSearch\Models\CacheModels.cs'
$dom = '.\src\BeaverSearch\Services\RenderedDomLoader.cs'
$legacyPrice = '.\src\BeaverSearch\Services\SkinportPriceProvider.cs'

Need $market 'steamcommunity\.com/market/priceoverview' 'Steam Community Market priceoverview endpoint marker missing.'
Need $market 'currency=5' 'Steam Market RUB currency marker missing.'
Need $market 'PositiveTtl\s*=\s*TimeSpan\.FromMinutes\(10\)' 'Steam Market positive cache is not 10 minutes.'
Need $market 'new\(4, 4\)' 'Steam Market request gate is not 4.'
Need $valuation '_playerValuationGate\s*=\s*new\(4, 4\)' 'Heavy player valuation gate is not 4.'
Need $valuation 'PricingAvailable' 'False-zero price protection marker missing.'
Need $inventory '_inventoryRequestGate\s*=\s*new\(6, 6\)' 'Steam inventory request gate is not 6.'
Need $inventory 'ConfigureAwait\(false\)' 'Steam inventory off-dispatcher awaits missing.'
Need $cache 'PriceEngineVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*2' 'PriceEngineVersion 2 marker missing.'
Need $local 'CurrentPriceEngineVersion\s*=\s*2' 'LocalStore price-engine migration marker missing.'
Need $local 'FlushCacheLoopAsync' 'Debounced cache writer marker missing.'
Need $dom 'CacheTtl\s*=\s*TimeSpan\.FromSeconds\(75\)' 'Browser probe cache TTL is not 75 seconds.'
Need $dom 'BrowserGate\s*=\s*new\(1, 1\)' 'Browser probe concurrency is not 1.'
Need $dom 'ProcessPriorityClass\.BelowNormal' 'Browser BelowNormal priority marker missing.'
Need $dom 'FragmentScript' 'Compact DOM fragment capture marker missing.'
Need $dom 'liteChars' 'Compact DOM diagnostics marker missing.'
Need $legacyPrice 'SteamMarketPriceProvider' 'Legacy price facade does not delegate to Steam Market.'

$serviceText = (Get-ChildItem '.\src\BeaverSearch\Services' -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }) -join "`n"
if ($serviceText -match 'api\.skinport\.com/v1/items') { $issues.Add('Full Skinport catalog endpoint is active again; this hotfix forbids it.') | Out-Null }

if ($issues.Count -gt 0) {
    Write-Host 'BeaverSearch HOTFIX PREFLIGHT: FAIL'
    foreach ($i in $issues) { Write-Host "- $i" }
    exit 1
}

Write-Host 'BeaverSearch HOTFIX PREFLIGHT: PASS'
Write-Host '- Steam Market RUB selective pricing: checked'
Write-Host '- false-zero 24h cache migration: checked'
Write-Host '- valuation concurrency 4 / inventory requests 6: checked'
Write-Host '- debounced cache writes: checked'
Write-Host '- browser probe: 1 process / 75s cache / compact DOM / BelowNormal: checked'
exit 0
