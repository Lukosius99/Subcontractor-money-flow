# Start-MoneyFlow.ps1 — Start the Monthly Money Flow service
# Run as Administrator

$service = "MoneyFlow"
$svc = Get-Service $service -ErrorAction SilentlyContinue

if ($null -eq $svc) {
    Write-Host "Service 'MoneyFlow' not found. Run the install script first." -ForegroundColor Red
} elseif ($svc.Status -eq "Running") {
    Write-Host "MoneyFlow is already running." -ForegroundColor Green
} else {
    Start-Service $service
    Start-Sleep -Seconds 2
    $newStatus = (Get-Service $service).Status
    if ($newStatus -eq "Running") {
        Write-Host "MoneyFlow started successfully." -ForegroundColor Green
        try {
            $r = Invoke-WebRequest http://127.0.0.1:5000/ -UseBasicParsing -TimeoutSec 5
            Write-Host "HTTP check: $($r.StatusCode)" -ForegroundColor Green
        } catch {
            Write-Host "HTTP check failed - service may still be warming up." -ForegroundColor Yellow
        }
    } else {
        Write-Host "MoneyFlow failed to start. Status: $newStatus" -ForegroundColor Red
    }
}

Read-Host "Press Enter to close"
