#Requires -Version 5.1
<#
.SYNOPSIS
  Build and deploy the rfid-anti-theft-gate-bridge on the mini PC.

.DESCRIPTION
  Builds gate-tcp-test with the HAX 2.1.2.0 SDK set (v2 mode), verifies the
  SDK against captured gate packets (--check-sdk), and restarts the Windows
  service if it already exists.

.PARAMETER ServiceName
  Name of the NSSM Windows service that runs the gate bridge. Default: rfid-anti-theft-gate-bridge.

.PARAMETER SkipCheck
  Skip running 'gate-tcp-test.exe --check-sdk' after building.

.EXAMPLE
  .\deploy.ps1
  .\deploy.ps1 -SkipCheck
#>
param(
  [string]$ServiceName = "rfid-anti-theft-gate-bridge",
  [switch]$SkipCheck
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet not found on PATH. Install the .NET 8 SDK."
}

$appDir = Join-Path $PSScriptRoot "gate-tcp-test"
$configPath = Join-Path $appDir "gate-bridge.json"
$examplePath = Join-Path $appDir "gate-bridge.example.json"

if (-not (Test-Path $configPath)) {
  throw "gate-bridge.json not found in $appDir. Copy it from $examplePath and set serverUrl before deploying."
}

$svcRunning = $false
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
  $svc = Get-Service -Name $ServiceName
  if ($svc.Status -eq "Running") {
    Write-Host "[gate-bridge] Stopping running service '$ServiceName' before build..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
    $svcRunning = $true
  }
}

Write-Host "[gate-bridge] Building gate-tcp-test (HaxSdk=2.1.2.0)..." -ForegroundColor Cyan
Push-Location $appDir
try {
  dotnet build -p:HaxSdk=2.1.2.0
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
  }
} finally {
  Pop-Location
}

$exeDir = Join-Path $appDir "bin\hax-2.1.2.0\Debug\net8.0\win-x86"
$exePath = Join-Path $exeDir "gate-tcp-test.exe"
if (-not (Test-Path $exePath)) {
  throw "Build did not produce $exePath. Check the build output above."
}

# Ensure the latest gate-bridge.json is copied next to the executable
Copy-Item -Path $configPath -Destination (Join-Path $exeDir "gate-bridge.json") -Force

if (-not $SkipCheck) {
  Write-Host "[gate-bridge] Verifying HAX 2.1.2.0 SDK (--check-sdk)..." -ForegroundColor Cyan
  & $exePath --check-sdk
  if ($LASTEXITCODE -ne 0) {
    throw "SDK self-check (--check-sdk) failed with exit code $LASTEXITCODE."
  }
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
  Write-Host "[gate-bridge] Starting service '$ServiceName'..." -ForegroundColor Cyan
  Start-Service -Name $ServiceName
  Write-Host "[gate-bridge] Deployed. Service '$ServiceName' is running." -ForegroundColor Green
} else {
  Write-Host "[gate-bridge] Built to $exeDir. Service '$ServiceName' not installed yet -" -ForegroundColor Yellow
  Write-Host "              run ..\setup-services.ps1 (elevated) to register it." -ForegroundColor Yellow
}
