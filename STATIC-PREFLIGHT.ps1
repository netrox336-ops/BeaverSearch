$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$issues = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]
$root = (Get-Location).Path
$xamlFiles = Get-ChildItem -Path '.\src\BeaverSearch' -Recurse -Filter '*.xaml' -File
$csFiles = Get-ChildItem -Path '.\src\BeaverSearch' -Recurse -Filter '*.cs' -File

function Add-Issue([string]$message) { $script:issues.Add($message) | Out-Null }

# 1) Every XAML must be valid XML.
foreach ($file in $xamlFiles) {
    try { [xml](Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8) | Out-Null }
    catch { Add-Issue "XAML XML parse failed: $($file.FullName.Substring($root.Length + 1)) :: $($_.Exception.Message)" }
}

$notes.Add("XAML XML parsed: $($xamlFiles.Count)/$($xamlFiles.Count)") | Out-Null

# 1b) WPF/XAML compile trap: a dependency property cannot be assigned both
# as an XML attribute and as an explicit property element on the same object.
$duplicatePropertyAssignments = 0
foreach ($file in $xamlFiles) {
    try {
        [xml]$doc = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        $allElements = $doc.SelectNodes('//*')
        foreach ($element in $allElements) {
            if ($null -eq $element.Attributes) { continue }
            $ownerName = $element.LocalName
            $attributeNames = New-Object 'System.Collections.Generic.HashSet[string]'
            foreach ($attr in $element.Attributes) {
                if ($attr.Prefix -eq 'xmlns' -or $attr.Name -eq 'xmlns') { continue }
                [void]$attributeNames.Add($attr.LocalName)
            }
            foreach ($child in $element.ChildNodes) {
                if ($child.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
                $local = $child.LocalName
                $prefix = "$ownerName."
                if (-not $local.StartsWith($prefix, [System.StringComparison]::Ordinal)) { continue }
                $propertyName = $local.Substring($prefix.Length)
                if ($attributeNames.Contains($propertyName)) {
                    $duplicatePropertyAssignments++
                    Add-Issue "Duplicate XAML property assignment in $($file.Name): <$ownerName> sets '$propertyName' both as an attribute and a property element."
                }
            }
        }
    } catch {
        # XML parse failures are already reported by check 1.
    }
}
$notes.Add("Duplicate attribute/property-element assignments: $duplicatePropertyAssignments") | Out-Null

# 2) StaticResource keys must resolve inside project XAML.
$allXaml = ($xamlFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
$keyMatches = [regex]::Matches($allXaml, 'x:Key\s*=\s*"([^"]+)"')
$keys = @{}
$keyCounts = @{}
foreach ($m in $keyMatches) {
    $key = $m.Groups[1].Value
    $keys[$key] = $true
    if ($keyCounts.ContainsKey($key)) { $keyCounts[$key]++ } else { $keyCounts[$key] = 1 }
}
foreach ($entry in $keyCounts.GetEnumerator()) {
    if ($entry.Value -gt 1) { Add-Issue "Duplicate ResourceDictionary key: $($entry.Key)" }
}
$resourceRefs = [regex]::Matches($allXaml, '\{StaticResource\s+([^\s,\}\"]+)')
foreach ($m in $resourceRefs) {
    $key = $m.Groups[1].Value
    if (-not $keys.ContainsKey($key)) { Add-Issue "Missing StaticResource key: $key" }
}
$notes.Add("StaticResource refs: $($resourceRefs.Count), project keys: $($keys.Count)") | Out-Null

# 3) Referenced packaged assets must exist.
$assetMatches = [regex]::Matches($allXaml, '(?:Source|Icon)\s*=\s*"/Assets/([^"]+)"')
foreach ($m in $assetMatches) {
    $rel = $m.Groups[1].Value.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $path = Join-Path '.\src\BeaverSearch\Assets' $rel
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Add-Issue "Missing asset: /Assets/$($m.Groups[1].Value)" }
}
$notes.Add("Referenced packaged assets: $($assetMatches.Count)") | Out-Null

# 4) Event handler attributes must have a method in matching code-behind.
$eventNames = 'Click|Checked|Unchecked|SelectionChanged|MouseLeftButtonDown|MouseDoubleClick|PreviewMouseWheel|PreviewKeyDown|Loaded|Closing|StateChanged|MouseDown|MouseMove|MouseUp|TextChanged|DropDownOpened|DropDownClosed'
$handlerCount = 0
foreach ($xaml in $xamlFiles) {
    $text = Get-Content -LiteralPath $xaml.FullName -Raw -Encoding UTF8
    $eventPattern = '(?:\s|<)(?:' + $eventNames + ')\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"'
    $matches = [regex]::Matches($text, $eventPattern)
    if ($matches.Count -eq 0) { continue }
    $codeBehind = $xaml.FullName + '.cs'
    $code = if (Test-Path $codeBehind) { Get-Content -LiteralPath $codeBehind -Raw -Encoding UTF8 } else { '' }
    foreach ($m in $matches) {
        $handlerCount++
        $name = $m.Groups[1].Value
        if ($code -notmatch "\b$([regex]::Escape($name))\s*\(") {
            Add-Issue "Event handler '$name' not found for $($xaml.Name)"
        }
    }
}
$notes.Add("Event handlers checked: $handlerCount") | Out-Null

# 4b) C# compile trap: async methods cannot have ref/out parameters (CS1988).
$asyncRefCount = 0
foreach ($file in $csFiles) {
    $code = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $matches = [regex]::Matches($code, '\basync\b[^\{;]{0,700}\([^\)]*\b(?:ref|out)\s+[A-Za-z_][A-Za-z0-9_<>,\.\?\[\]]*\s+[A-Za-z_][A-Za-z0-9_]*', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    foreach ($m in $matches) {
        $asyncRefCount++
        Add-Issue "C# async ref/out compile trap (CS1988) in $($file.FullName.Substring($root.Length + 1))."
    }
}
$notes.Add("C# async ref/out signatures: $asyncRefCount") | Out-Null

# 5) Known WPF runtime traps from previous crashes.
$allowedPanning = @('None','HorizontalOnly','VerticalOnly','Both')
foreach ($m in [regex]::Matches($allXaml, 'PanningMode\s*=\s*"([^"]+)"')) {
    if ($allowedPanning -notcontains $m.Groups[1].Value) { Add-Issue "Invalid PanningMode: $($m.Groups[1].Value)" }
}
if ($allXaml -match '<Window\.RenderTransform\b') { Add-Issue 'Window.RenderTransform is forbidden; animate an inner root instead.' }

foreach ($xaml in $xamlFiles) {
    $text = Get-Content -LiteralPath $xaml.FullName -Raw -Encoding UTF8
    $names = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($m in [regex]::Matches($text, '(?:x:Name|Name)\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"')) { [void]$names.Add($m.Groups[1].Value) }
    foreach ($m in [regex]::Matches($text, 'Storyboard\.TargetName\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"')) {
        if (-not $names.Contains($m.Groups[1].Value)) { Add-Issue "Storyboard.TargetName '$($m.Groups[1].Value)' missing in $($xaml.Name)" }
    }
}

# Read-only diagnostic ProgressBar bindings must not try TwoWay writes.
foreach ($m in [regex]::Matches($allXaml, '<ProgressBar\b[^>]*(?:Value|Maximum)\s*=\s*"\{Binding\s+([^\"]+)\}"[^>]*/?>')) {
    if ($m.Groups[1].Value -notmatch 'Mode\s*=\s*OneWay') { Add-Issue "ProgressBar binding is not explicitly OneWay: $($m.Value)" }
}

# Writable-target binding audit. These controls default to TwoWay in WPF, so they
# must only point at properties that expose setters.
$writablePaths = @(
    'NewServerAddress','ManualSteamId',
    'Settings.Cs2Min','Settings.Cs2Max',
    'Settings.DotaMin','Settings.DotaMax','Settings.RustMin','Settings.RustMax',
    'Settings.Language','Settings.PollSeconds','Settings.RequestTimeoutSeconds','Settings.CsFloatApiKey'
)
$editablePatterns = @(
    '<TextBox\b[^>]*Text\s*=\s*"\{Binding\s+([^\s,\}]+)',
    '<ToggleButton\b[^>]*IsChecked\s*=\s*"\{Binding\s+([^\s,\}]+)',
    '<CheckBox\b[^>]*IsChecked\s*=\s*"\{Binding\s+([^\s,\}]+)',
    '<ComboBox\b[^>]*(?:SelectedValue|SelectedItem)\s*=\s*"\{Binding\s+([^\s,\}]+)'
)
foreach ($pattern in $editablePatterns) {
    foreach ($m in [regex]::Matches($allXaml, $pattern)) {
        $path = $m.Groups[1].Value
        if ($path -eq 'IsDropDownOpen') { continue }
        if ($writablePaths -notcontains $path) { Add-Issue "Potential read-only/default-TwoWay binding: $path" }
    }
}

# 6) Initial tab and old raster navigation icon cleanup.
$mainWindow = Get-Content '.\src\BeaverSearch\MainWindow.xaml' -Raw -Encoding UTF8
if ($mainWindow -notmatch 'x:Name="MainTabs"[^>]*SelectedIndex="0"') { Add-Issue 'MainTabs must start at SelectedIndex=0.' }
$firstTab = [regex]::Match($mainWindow, '<TabItem[\s\S]*?</TabItem>')
if (-not $firstTab.Success -or $firstTab.Value -notmatch 'MonitorView') { Add-Issue 'First TabItem does not materialize MonitorView.' }
if (Test-Path '.\src\BeaverSearch\Assets\Icons') { Add-Issue 'Old raster Assets\Icons directory still exists.' }
if ($allXaml -match '/Assets/Icons/') { Add-Issue 'Old raster navigation icon reference still exists in XAML.' }

# 7) 0.4.1 FixSteamID architecture: yooma.su + CYBERSHOKE exact identity discovery.
$vmPath = '.\src\BeaverSearch\ViewModels\MainViewModel.cs'
$yoomaPath = '.\src\BeaverSearch\Services\YoomaClient.cs'
$cyberPath = '.\src\BeaverSearch\Services\CybershokeClient.cs'
$domPath = '.\src\BeaverSearch\Services\RenderedDomLoader.cs'
$steamIdPath = '.\src\BeaverSearch\Services\SteamIdentityParser.cs'

foreach ($pair in @(
    @($yoomaPath, 'YoomaClient.cs'),
    @($cyberPath, 'CybershokeClient.cs'),
    @($domPath, 'RenderedDomLoader.cs'),
    @($steamIdPath, 'SteamIdentityParser.cs')
)) {
    if (-not (Test-Path -LiteralPath $pair[0] -PathType Leaf)) { Add-Issue "$($pair[1]) is missing." }
}

if (Test-Path -LiteralPath $yoomaPath -PathType Leaf) {
    $yoomaCode = Get-Content -LiteralPath $yoomaPath -Raw -Encoding UTF8
    if ($yoomaCode -notmatch 'profile/') { Add-Issue 'YoomaClient profile-route parser marker is missing.' }
    if ($yoomaCode -notmatch 'card/') { Add-Issue 'YoomaClient /card/<SteamID64> parser marker is missing.' }
    if ($yoomaCode -notmatch 'RenderedDomLoader') { Add-Issue 'YoomaClient rendered-DOM/API fallback is missing.' }
    if ($yoomaCode -notmatch 'SteamAccountAttributeRegex') { Add-Issue 'YoomaClient explicit Steam/account element parser is missing.' }
    if ($yoomaCode -notmatch '/ru/servers/awp/') { Add-Issue 'YoomaClient current mode routes are missing.' }
    if ($yoomaCode -notmatch 'new\(4, 4\)') { Add-Issue 'Yooma page concurrency gate is not 4.' }
}

if (Test-Path -LiteralPath $cyberPath -PathType Leaf) {
    $cyberCode = Get-Content -LiteralPath $cyberPath -Raw -Encoding UTF8
    if ($cyberCode -notmatch 'cybershoke\.net') { Add-Issue 'CYBERSHOKE base endpoint marker is missing.' }
    if ($cyberCode -notmatch '/ru/cs2/servers/dm') { Add-Issue 'CYBERSHOKE CS2 mode routes are missing.' }
    if ($cyberCode -notmatch 'RenderedDomLoader') { Add-Issue 'CYBERSHOKE rendered-DOM/API reader is missing.' }
    if ($cyberCode -notmatch 'SteamAccountAttributeRegex') { Add-Issue 'CYBERSHOKE explicit Steam/account element parser is missing.' }
    if ($cyberCode -notmatch 'RenderBatchSize\s*=\s*8') { Add-Issue 'CYBERSHOKE rolling render batch is not 8.' }
}

if (Test-Path -LiteralPath $domPath -PathType Leaf) {
    $domCode = Get-Content -LiteralPath $domPath -Raw -Encoding UTF8
    if ($domCode -notmatch '--dump-dom') { Add-Issue 'Headless browser dump-dom fallback marker is missing.' }
    if ($domCode -notmatch '--remote-debugging-port=') { Add-Issue 'Chromium DevTools remote-debugging marker is missing.' }
    if ($domCode -notmatch 'Network\.responseReceived') { Add-Issue 'CDP Network.responseReceived capture is missing.' }
    if ($domCode -notmatch 'Network\.getResponseBody') { Add-Issue 'CDP Network.getResponseBody capture is missing.' }
    if ($domCode -notmatch 'Network\.webSocketFrameReceived') { Add-Issue 'CDP WebSocket frame capture is missing.' }
    if ($domCode -notmatch 'BrowserGate\.WaitAsync\(TimeSpan\.FromMilliseconds\(180\)') { Add-Issue 'Non-blocking browser probe gate marker is missing.' }
    if ($domCode -notmatch 'Microsoft\\Edge|Microsoft\s+Edge') { Add-Issue 'Microsoft Edge discovery marker is missing.' }
    if ($domCode -notmatch 'Google\\Chrome|Google\s+Chrome') { Add-Issue 'Google Chrome discovery marker is missing.' }
    if ($domCode -notmatch '--user-data-dir=') { Add-Issue 'Disposable browser profile marker is missing.' }
}

if (Test-Path -LiteralPath $steamIdPath -PathType Leaf) {
    $steamCode = Get-Content -LiteralPath $steamIdPath -Raw -Encoding UTF8
    if ($steamCode -notmatch '76561197960265728') { Add-Issue 'Steam AccountID -> SteamID64 conversion base is missing.' }
    if ($steamCode -notmatch 'STEAM_') { Add-Issue 'Steam2 normalization marker is missing.' }
    if ($steamCode -notmatch 'U:1:') { Add-Issue 'Steam3 normalization marker is missing.' }
}

if (-not (Test-Path -LiteralPath $vmPath -PathType Leaf)) {
    Add-Issue 'MainViewModel.cs is missing.'
} else {
    $vmCode = Get-Content -LiteralPath $vmPath -Raw -Encoding UTF8
    if ($vmCode -notmatch 'private readonly YoomaClient _yooma') { Add-Issue 'MainViewModel is not wired to YoomaClient.' }
    if ($vmCode -notmatch 'private readonly CybershokeClient _cybershoke') { Add-Issue 'MainViewModel is not wired to CybershokeClient.' }
    if ($vmCode -notmatch 'await PollCommunitySourcesAsync\(ct\)') { Add-Issue 'Monitoring loop does not call PollCommunitySourcesAsync.' }
    if ($vmCode -match 'A2sClient|GameMonitoringResolver|UseGameMonitoring') { Add-Issue 'Legacy A2S/GAMEMONITORING resolver is still referenced by MainViewModel.' }
    if ($vmCode -match 'forceRefresh\s*:\s*true') { Add-Issue 'MainViewModel bypasses source refresh caches.' }
    if ($vmCode -notmatch 'TimeSpan\.FromHours\(24\)') { Add-Issue '24h SteamID scan TTL marker is missing.' }
    if ($vmCode -notmatch 'new\(8, 8\)') { Add-Issue 'Inventory scan concurrency gate is not 8.' }
}

if (Test-Path '.\src\BeaverSearch\Services\A2sClient.cs') { Add-Issue 'Legacy A2sClient.cs still exists in 0.4.1 source.' }
if (Test-Path '.\src\BeaverSearch\Services\GameMonitoringResolver.cs') { Add-Issue 'Legacy GameMonitoringResolver.cs still exists in 0.4.1 source.' }
if ($allXaml -match 'GAMEMONITORING|A2S Direct|Settings\.UseGameMonitoring') { Add-Issue 'Legacy resolver/provider text or binding remains in active XAML.' }
if ($allXaml -notmatch 'YoomaLivePlayers') { Add-Issue 'Diagnostics yooma live-player counter is missing.' }
if ($allXaml -notmatch 'CybershokeLivePlayers') { Add-Issue 'Diagnostics CYBERSHOKE live-player counter is missing.' }
if ($allXaml -notmatch 'YoomaPagesLoaded') { Add-Issue 'Diagnostics yooma page progress is missing.' }
if ($allXaml -notmatch 'CybershokePagesLoaded') { Add-Issue 'Diagnostics CYBERSHOKE page progress is missing.' }
$notes.Add('FixSteamID pipeline: yooma.su + CYBERSHOKE DOM/XHR/WebSocket -> exact SteamID checked') | Out-Null

# 8) Version/DPI/release support markers.
$csproj = Get-Content '.\src\BeaverSearch\BeaverSearch.csproj' -Raw -Encoding UTF8
$manifest = Get-Content '.\src\BeaverSearch\app.manifest' -Raw -Encoding UTF8
if ($csproj -notmatch '<Version>0\.4\.1</Version>') { Add-Issue 'csproj Version is not 0.4.1.' }
if ($csproj -notmatch '<AssemblyVersion>0\.4\.1\.0</AssemblyVersion>') { Add-Issue 'AssemblyVersion is not 0.4.1.0.' }
if ($csproj -notmatch '<FileVersion>0\.4\.1\.0</FileVersion>') { Add-Issue 'FileVersion is not 0.4.1.0.' }
if ($manifest -notmatch 'PerMonitorV2') { Add-Issue 'PerMonitorV2 DPI awareness missing.' }

$required = @('README.md','CHANGELOG.md','TEST-CHECKLIST.md','THIRD-PARTY-NOTICES.md','START.bat','DEBUG-START.bat','build-release.ps1','BUILD-RELEASE.bat')
foreach ($f in $required) { if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Add-Issue "Required release file missing: $f" } }
if (Test-Path '.\.github\workflows') { Add-Issue 'GitHub Actions workflows must not be present in this development repository.' }

$result = New-Object System.Collections.Generic.List[string]
if ($issues.Count -eq 0) { $result.Add('BeaverSearch v0.4.1 FixSteamID STATIC PREFLIGHT: PASS') | Out-Null }
else { $result.Add('BeaverSearch v0.4.1 FixSteamID STATIC PREFLIGHT: FAIL') | Out-Null }
$result.Add('') | Out-Null
$result.Add('Checks:') | Out-Null
foreach ($n in $notes) { $result.Add("- $n") | Out-Null }
$result.Add('- Duplicate XAML property assignment / PanningMode / TargetName / Window.RenderTransform: checked') | Out-Null
$result.Add('- Binding modes: writable inputs + computed ProgressBar OneWay checked') | Out-Null
$result.Add('- C# async ref/out compile trap (CS1988): checked') | Out-Null
$result.Add('- MainTabs initial page: MonitorView checked') | Out-Null
$result.Add('- FixSteamID yooma.su + CYBERSHOKE DOM/API/WebSocket pipeline: checked') | Out-Null
$result.Add('- GitHub Actions workflows: forbidden/checked') | Out-Null
$result.Add('- Version / PerMonitorV2 / release files: checked') | Out-Null
$result.Add('') | Out-Null
if ($issues.Count -eq 0) {
    $result.Add('No static preflight issues detected.') | Out-Null
} else {
    $result.Add('Issues:') | Out-Null
    foreach ($i in $issues) { $result.Add("- $i") | Out-Null }
}
$result.Add('') | Out-Null
$result.Add('NOTE: STATIC-PREFLIGHT does not replace the native Windows WPF compiler/runtime test. START.bat performs that build before launch.') | Out-Null
$result | Set-Content -LiteralPath '.\STATIC-PREFLIGHT.txt' -Encoding UTF8
$result | ForEach-Object { Write-Host $_ }
if ($issues.Count -gt 0) { exit 1 }
exit 0
