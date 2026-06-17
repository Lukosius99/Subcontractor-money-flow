# Deploy-MoneyFlow.ps1 — One-click deploy for the PADS MoneyFlow app
# ---------------------------------------------------------------------------
# Run this ONCE on a fresh machine after cloning/downloading the repo:
#   Right-click  ->  Run with PowerShell   (it will request Administrator)
#
# What it does, in order:
#   1. Re-launches itself elevated if needed (service install requires admin).
#   2. Verifies the .NET 10 SDK is installed.
#   3. Publishes the app (Release) into  <repo>\publish .
#   4. Ensures C:\ProgramData\PADS\MoneyFlow exists and, ONLY if no database is
#      already there, restores the bundled snapshot from  <repo>\seed .
#      => Re-running on a machine that already has data never overwrites it.
#   5. Installs (or updates) the "MoneyFlow" Windows service pointing at the
#      published exe, with ASPNETCORE_ENVIRONMENT=Production so it binds
#      port 5000 and uses the ProgramData database.
#   6. Starts the service and verifies http://localhost:5000 responds.
# ---------------------------------------------------------------------------

$ErrorActionPreference = "Stop"

# --- 1. Elevate to Administrator -------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting Administrator rights..." -ForegroundColor Yellow
    Start-Process powershell.exe `
        -Verb RunAs `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$RepoRoot   = $PSScriptRoot
$PublishDir = Join-Path $RepoRoot "publish"
$Csproj     = Join-Path $RepoRoot "PADS.MoneyFlow.Api.csproj"
$ExePath    = Join-Path $PublishDir "PADS.MoneyFlow.Api.exe"

$ServiceName = "MoneyFlow"
$DisplayName = "PADS Monthly Money Flow"
$Description  = "Monthly Money Flow internal finance tracker"

$DataDir = "C:\ProgramData\PADS\MoneyFlow"
$DbFile  = Join-Path $DataDir "monthly-money-flow.db"
$SeedDb  = Join-Path $RepoRoot "seed\monthly-money-flow.db"

Write-Host "MoneyFlow deploy starting" -ForegroundColor Cyan
Write-Host "  Repo:    $RepoRoot"
Write-Host "  Publish: $PublishDir"
Write-Host ""

# --- 2. Verify .NET 10 SDK --------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Write-Host "ERROR: .NET SDK not found." -ForegroundColor Red
    Write-Host "Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 then re-run." -ForegroundColor Yellow
    Read-Host "Press Enter to close"; exit 1
}
$hasNet10 = (& dotnet --list-sdks) | Where-Object { $_ -like "10.*" }
if (-not $hasNet10) {
    Write-Host "ERROR: .NET 10 SDK is required but not installed." -ForegroundColor Red
    Write-Host "Installed SDKs:" -ForegroundColor Yellow
    & dotnet --list-sdks
    Write-Host "Get .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0 then re-run." -ForegroundColor Yellow
    Read-Host "Press Enter to close"; exit 1
}
Write-Host "[OK] .NET 10 SDK present." -ForegroundColor Green

# --- Stop the service first so the publish target isn't locked --------------
$existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping existing '$ServiceName' service so files can be replaced..." -ForegroundColor Yellow
    Stop-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus("Stopped", "00:00:30")
}

# --- 3. Publish -------------------------------------------------------------
Write-Host "Publishing (Release)..." -ForegroundColor Cyan
& dotnet publish $Csproj -c Release -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: expected exe not found at $ExePath" -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}
Write-Host "[OK] Published to $PublishDir" -ForegroundColor Green

# --- 4. Data: create dir, restore seed ONLY if no DB exists -----------------
if (-not (Test-Path $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
}
if (Test-Path $DbFile) {
    Write-Host "[OK] Existing database found at $DbFile — left untouched (your data is safe)." -ForegroundColor Green
} else {
    if (Test-Path $SeedDb) {
        Copy-Item $SeedDb $DbFile
        Write-Host "[OK] Restored bundled database snapshot to $DbFile" -ForegroundColor Green
    } else {
        Write-Host "WARNING: no existing DB and no seed snapshot found — app will start with an empty database." -ForegroundColor Yellow
    }
}

# --- 5. Install / update the Windows service --------------------------------
if ($null -eq $existing) {
    Write-Host "Installing '$ServiceName' service..." -ForegroundColor Cyan
    New-Service -Name $ServiceName `
                -BinaryPathName "`"$ExePath`"" `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic | Out-Null
} else {
    Write-Host "Updating existing '$ServiceName' service binary path..." -ForegroundColor Cyan
    & sc.exe config $ServiceName binPath= "`"$ExePath`"" start= auto | Out-Null
}

# Set ASPNETCORE_ENVIRONMENT=Production for THIS service only (service-scoped,
# does not pollute machine-wide env). Required so appsettings.Production.json
# is applied (port 5000 + the ProgramData database path).
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $svcKey -Name "Environment" `
    -Value @("ASPNETCORE_ENVIRONMENT=Production") `
    -PropertyType MultiString -Force | Out-Null
Write-Host "[OK] Service configured (ASPNETCORE_ENVIRONMENT=Production)." -ForegroundColor Green

# --- 6. Start + verify ------------------------------------------------------
Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service $ServiceName
Start-Sleep -Seconds 4

$status = (Get-Service $ServiceName).Status
if ($status -ne "Running") {
    Write-Host "ERROR: service did not start (status: $status). Check Event Viewer." -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}

try {
    $resp = Invoke-WebRequest -Uri "http://localhost:5000" -UseBasicParsing -TimeoutSec 10
    Write-Host ""
    Write-Host "SUCCESS — MoneyFlow is running. HTTP $($resp.StatusCode) from http://localhost:5000" -ForegroundColor Green
} catch {
    Write-Host "Service is running but http://localhost:5000 did not respond yet." -ForegroundColor Yellow
    Write-Host "Give it a few seconds and open http://localhost:5000 in a browser." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Open http://localhost:5000 to use the app." -ForegroundColor Cyan
Read-Host "Press Enter to close"
