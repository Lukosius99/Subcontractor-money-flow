# Stop-MoneyFlow.ps1 — Stop the Monthly Money Flow service
# Run as Administrator

$service = "MoneyFlow"
$svc = Get-Service $service -ErrorAction SilentlyContinue

if ($null -eq $svc) {
    Write-Host "Service 'MoneyFlow' not found." -ForegroundColor Yellow
} elseif ($svc.Status -eq "Stopped") {
    Write-Host "MoneyFlow is already stopped." -ForegroundColor Yellow
} else {
    Stop-Service $service
    Start-Sleep -Seconds 2
    Write-Host "MoneyFlow stopped." -ForegroundColor Green
}
