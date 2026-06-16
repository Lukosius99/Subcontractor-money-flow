# Clean-MoneyFlowDB.ps1 — Backup and wipe the MoneyFlow SQLite database
# Run as Administrator
# The app will recreate a fresh empty database on next start.

$dbDir     = "C:\ProgramData\PADS\MoneyFlow"
$dbPath    = "$dbDir\monthly-money-flow.db"
$dbShm     = "$dbDir\monthly-money-flow.db-shm"
$dbWal     = "$dbDir\monthly-money-flow.db-wal"
$backupDir = "$dbDir\Backups"
$stamp     = Get-Date -Format "yyyyMMdd-HHmm"

# -- 1. Backup ----------------------------------------------------------------
if (Test-Path $dbPath) {
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $backupPath = "$backupDir\monthly-money-flow-backup-$stamp.db"
    Copy-Item $dbPath $backupPath
    Write-Host "Backup saved to: $backupPath" -ForegroundColor Cyan
} else {
    Write-Host "No database file found - nothing to back up." -ForegroundColor Yellow
}

# -- 2. Stop service so the file is not locked --------------------------------
$svc = Get-Service MoneyFlow -ErrorAction SilentlyContinue
if ($null -ne $svc -and $svc.Status -ne "Stopped") {
    Stop-Service MoneyFlow
    Start-Sleep -Seconds 2
    Write-Host "MoneyFlow stopped." -ForegroundColor Green
}

# -- 3. Delete database files -------------------------------------------------
Remove-Item $dbPath -ErrorAction SilentlyContinue
Remove-Item $dbShm  -ErrorAction SilentlyContinue
Remove-Item $dbWal  -ErrorAction SilentlyContinue
Write-Host "Database files deleted." -ForegroundColor Green

# -- 4. Restart — app recreates DB schema automatically ----------------------
if ($null -ne $svc) {
    Start-Service MoneyFlow
    Start-Sleep -Seconds 2
    Write-Host "MoneyFlow restarted with a clean database." -ForegroundColor Green
    Get-Service MoneyFlow | Format-Table Name, Status -AutoSize
} else {
    Write-Host "Service not installed - start the app manually when ready." -ForegroundColor Yellow
}
