$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host "dotnet is already installed:" -ForegroundColor Green
    dotnet --version
    exit 0
}

$dotnetExe = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (Test-Path $dotnetExe) {
    Write-Host ".NET is installed at $dotnetExe" -ForegroundColor Green
    & $dotnetExe --version
    exit 0
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host "winget was not found. Install .NET 8 SDK (x64) from https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    exit 1
}

Write-Host "Installing official .NET 8 SDK via winget..." -ForegroundColor Cyan
winget install --id Microsoft.DotNet.SDK.8 --source winget --accept-package-agreements --accept-source-agreements

if (Test-Path $dotnetExe) {
    Write-Host ".NET 8 SDK installation finished:" -ForegroundColor Green
    & $dotnetExe --version
    Write-Host "You can now run START.bat or run-dev.ps1." -ForegroundColor Green
    exit 0
}

Write-Host "Installation finished, but dotnet is not visible yet. Close this terminal, open it again, and run START.bat." -ForegroundColor Yellow
