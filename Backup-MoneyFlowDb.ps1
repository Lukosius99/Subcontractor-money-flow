[CmdletBinding()]
param(
    # API raktas (tas pats kaip importu). Jei nenurodyta - paklausiama paleidus.
    [string]$ApiKey,
    # Veikiancios programos adresas.
    [string]$BaseUrl = 'http://localhost:5000',
    # Daro tik lokalia kopija, nesiuncia i GitHub.
    [switch]$SkipGitHub,
    # Nelaukia Enter pabaigoje (automatiniam paleidimui; kartu nurodykite -ApiKey).
    [switch]$NoPause
)

# Administratoriaus teisiu NEREIKIA: kopija daroma per programos API (SQLite VACUUM INTO),
# todel servisas nestabdomas. Jei servisas isjungtas, failas tiesiog nukopijuojamas.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Wait-Exit([int]$Code) {
    if (-not $NoPause) { Read-Host 'Paspauskite Enter, kad uždarytumėte' | Out-Null }
    exit $Code
}
function Stop-WithError([string]$Message) {
    Write-Host "`nKLAIDA: $Message" -ForegroundColor Red
    Wait-Exit 1
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$apiKeyFile = Join-Path $dataDirectory 'Configuration\api-key.txt'
$repoRoot = $PSScriptRoot
$repoBackupDirectory = Join-Path $repoRoot 'db-backups'
$keepCount = 30

Write-Host 'MoneyFlow DB atsarginė kopija' -ForegroundColor Green
Write-Host "Duomenų bazė: $liveDatabase"
Write-Host "Kopijos:      $backupsDirectory"

if (-not (Test-Path -LiteralPath $liveDatabase -PathType Leaf)) {
    Stop-WithError "Nerasta duomenų bazė: $liveDatabase"
}

# Ar programa veikia?
$serviceAlive = $false
try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 3
    if ($health.status -eq 'ok') { $serviceAlive = $true }
} catch { }

$backupPath = $null

if ($serviceAlive) {
    # Kopija per API - vientisa net vykstant irasymams, serviso stabdyti nereikia.
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        $fileKey = $null
        try {
            if (Test-Path -LiteralPath $apiKeyFile -PathType Leaf) {
                $fileKey = (Get-Content -LiteralPath $apiKeyFile -Raw).Trim()
            }
        } catch { }
        if ($fileKey) {
            $entered = Read-Host 'Įveskite API raktą (Enter – naudoti raktą iš Configuration\api-key.txt)'
            if ([string]::IsNullOrWhiteSpace($entered)) { $ApiKey = $fileKey } else { $ApiKey = $entered.Trim() }
        } else {
            $ApiKey = (Read-Host 'Įveskite API raktą').Trim()
        }
        if ([string]::IsNullOrWhiteSpace($ApiKey)) { Stop-WithError 'API raktas neįvestas.' }
    }

    Write-Step 'Kopijos kūrimas per programos API (servisas nestabdomas)'
    try {
        $result = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/maintenance/db-backup" `
            -Headers @{ 'X-Api-Key' = $ApiKey } -TimeoutSec 120
    } catch {
        $statusCode = 0
        if ($_.Exception.Response) { $statusCode = [int]$_.Exception.Response.StatusCode }
        switch ($statusCode) {
            401 { Stop-WithError 'Neteisingas API raktas.' }
            { $_ -in 404, 405 } { Stop-WithError "Veikianti programa dar neturi atsarginių kopijų endpoint'o. Paleiskite .\Deploy-MoneyFlow.ps1, kad įdiegtumėte naujausią versiją." }
            503 { Stop-WithError 'API raktas serveryje nesukonfigūruotas (žr. Set-MoneyFlowApiKey.ps1).' }
            default { Stop-WithError "Kopijos užklausa nepavyko (HTTP $statusCode): $($_.Exception.Message)" }
        }
    }
    $backupPath = $result.fullPath
    $backupSize = [math]::Round($result.sizeBytes / 1KB)
    Write-Host "[GERAI] Sukurta kopija: $($result.fileName) ($backupSize KB)" -ForegroundColor Green
    Write-Host ("Serveris ištrynė senų kopijų: {0} (laikoma naujausių: {1})" -f $result.prunedCount, $keepCount)
} else {
    # Servisas neveikia - niekas neraso i DB, todel failo kopija yra saugi ir be API.
    Write-Step 'Programa neatsako – DB failas kopijuojamas tiesiogiai'
    if (-not (Test-Path -LiteralPath $backupsDirectory)) {
        New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
    }
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $backupsDirectory "monthly-money-flow-backup-$timestamp.db"
    Copy-Item -LiteralPath $liveDatabase -Destination $backupPath
    foreach ($suffix in @('-wal', '-shm')) {
        if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
            Copy-Item -LiteralPath ($liveDatabase + $suffix) -Destination ($backupPath + $suffix)
        }
    }
    $backupSize = [math]::Round((Get-Item -LiteralPath $backupPath).Length / 1KB)
    Write-Host "[GERAI] Sukurta kopija: $(Split-Path -Leaf $backupPath) ($backupSize KB)" -ForegroundColor Green

    # Senu kopiju valymas (svetimu paskyru failai gali nepasiduoti - praleidziama su ispejimu).
    $oldBackups = @(Get-ChildItem -LiteralPath $backupsDirectory -Filter 'monthly-money-flow-backup-*.db' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $keepCount)
    $prunedCount = 0
    foreach ($oldBackup in $oldBackups) {
        try {
            foreach ($suffix in @('-wal', '-shm')) {
                if (Test-Path -LiteralPath ($oldBackup.FullName + $suffix)) {
                    Remove-Item -LiteralPath ($oldBackup.FullName + $suffix) -Force
                }
            }
            Remove-Item -LiteralPath $oldBackup.FullName -Force
            $prunedCount++
        } catch {
            Write-Host "DĖMESIO: nepavyko ištrinti senos kopijos $($oldBackup.Name) – praleidžiama." -ForegroundColor Yellow
        }
    }
    Write-Host ("Ištrinta senų kopijų: {0} (laikoma naujausių: {1})" -f $prunedCount, $keepCount)
}

if (-not $SkipGitHub) {
    Write-Step 'Kopijos siuntimas į GitHub (projekto repozitorija, katalogas db-backups)'
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) {
        Write-Host 'DĖMESIO: nerastas git – kopija liko tik lokaliai.' -ForegroundColor Yellow
    } elseif (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
        Write-Host "DĖMESIO: $repoRoot nėra git repozitorija (parsisiųsta kaip ZIP?) – kopija liko tik lokaliai." -ForegroundColor Yellow
    } else {
        & git -C $repoRoot pull --ff-only --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'DĖMESIO: nepavyko atsinaujinti repozitorijos (git pull). Bandoma siųsti vis tiek.' -ForegroundColor Yellow
        }

        # Repozitorijoje laikoma tik naujausia kopija – senesnės versijos lieka git istorijoje.
        if (-not (Test-Path -LiteralPath $repoBackupDirectory)) {
            New-Item -ItemType Directory -Path $repoBackupDirectory -Force | Out-Null
        }
        Get-ChildItem -LiteralPath $repoBackupDirectory -File |
            Where-Object { $_.Name -like 'monthly-money-flow-latest.db*' } |
            Remove-Item -Force
        $latestPath = Join-Path $repoBackupDirectory 'monthly-money-flow-latest.db'
        Copy-Item -LiteralPath $backupPath -Destination $latestPath
        foreach ($suffix in @('-wal', '-shm')) {
            if (Test-Path -LiteralPath ($backupPath + $suffix)) {
                Copy-Item -LiteralPath ($backupPath + $suffix) -Destination ($latestPath + $suffix)
            }
        }

        $changes = & git -C $repoRoot status --porcelain -- db-backups
        if (-not $changes) {
            Write-Host '[GERAI] GitHub kopija jau naujausia (duomenys nepasikeitė nuo praeito karto).' -ForegroundColor Green
        } else {
            $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $pushOk = $false
            & git -C $repoRoot add -A -- db-backups
            & git -C $repoRoot commit --quiet -m "DB backup $timestamp" -- db-backups
            if ($LASTEXITCODE -eq 0) {
                & git -C $repoRoot push --quiet origin HEAD
                if ($LASTEXITCODE -eq 0) { $pushOk = $true }
            }
            if ($pushOk) {
                Write-Host '[GERAI] Kopija išsiųsta į GitHub (db-backups/monthly-money-flow-latest.db).' -ForegroundColor Green
            } else {
                Write-Host 'DĖMESIO: išsiųsti į GitHub nepavyko – kopija liko lokaliai. Patikrinkite interneto ryšį / git prisijungimą.' -ForegroundColor Yellow
            }
        }
    }
}

Write-Host "`nAtsarginė kopija baigta: $backupPath" -ForegroundColor Green
Wait-Exit 0
