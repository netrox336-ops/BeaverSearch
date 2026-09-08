$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Running static preflight..." -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\STATIC-PREFLIGHT.ps1"
if ($LASTEXITCODE -ne 0) { throw "STATIC-PREFLIGHT failed with exit code $LASTEXITCODE" }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if ($dotnet) {
    $dotnetExe = $dotnet.Source
} else {
    $dotnetExe = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
}

if (-not (Test-Path $dotnetExe)) {
    Write-Host ".NET 8 SDK was not found. Run START.bat or install-dotnet-sdk.ps1 first." -ForegroundColor Red
    exit 1
}

$out = Join-Path $PSScriptRoot "release\win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

& $dotnetExe restore .\src\BeaverSearch\BeaverSearch.csproj
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

& $dotnetExe publish .\src\BeaverSearch\BeaverSearch.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $out 'BeaverSearch.exe'
if (-not (Test-Path $exe)) { throw "Publish finished without BeaverSearch.exe" }

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash
$hash | Set-Content -LiteralPath (Join-Path $out 'BeaverSearch.exe.sha256.txt') -Encoding ASCII
Write-Host "Release created at: $out" -ForegroundColor Green
Write-Host "BeaverSearch.exe SHA-256: $hash" -ForegroundColor Green
