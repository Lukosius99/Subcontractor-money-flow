# Install-MoneyFlow.ps1 — Register MoneyFlow as a Windows Service
# Run as Administrator — only needed once per machine

$serviceName = "MoneyFlow"
$exePath     = "C:\Apps\MoneyFlow\PADS.MoneyFlow.Api.exe"
$displayName = "PADS Monthly Money Flow"
$description = "Monthly Money Flow internal finance tracker"

# Check exe exists
if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Could not find $exePath" -ForegroundColor Red
    Write-Host "Make sure the app files are in C:\Apps\MoneyFlow before running this script." -ForegroundColor Yellow
    Read-Host "Press Enter to close"
    exit 1
}

# Check if already installed
$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Write-Host "Service '$serviceName' is already installed. Status: $($existing.Status)" -ForegroundColor Yellow
    Read-Host "Press Enter to close"
    exit 0
}

# Install
New-Service -Name $serviceName `
            -BinaryPathName $exePath `
            -DisplayName $displayName `
            -Description $description `
            -StartupType Automatic

Write-Host "Service installed successfully." -ForegroundColor Green

# Start it
Start-Service $serviceName
Start-Sleep -Seconds 3

$status = (Get-Service $serviceName).Status
if ($status -eq "Running") {
    Write-Host "MoneyFlow is running." -ForegroundColor Green
    Write-Host "Open http://localhost:5000 to verify." -ForegroundColor Cyan
} else {
    Write-Host "Service installed but failed to start. Status: $status" -ForegroundColor Red
    Write-Host "Check Windows Event Viewer for details." -ForegroundColor Yellow
}

Read-Host "Press Enter to close"
