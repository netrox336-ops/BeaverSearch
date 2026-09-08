$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Running static preflight..." -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\STATIC-PREFLIGHT.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

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

& $dotnetExe run --project .\src\BeaverSearch\BeaverSearch.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
