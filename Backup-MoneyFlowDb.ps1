[CmdletBinding()]
param(
    [string]$ApiKey,
    [string]$BaseUrl = 'http://localhost:5000',
    [switch]$SkipGitHub,
    [switch]$NoPause
)

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
function Get-PlainText([Security.SecureString]$SecureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$liveDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$backupsDirectory = Join-Path $dataDirectory 'Backups'
$apiKeyFile = Join-Path $dataDirectory 'Configuration\api-key.txt'
$passphraseFile = Join-Path $dataDirectory 'Configuration\backup-passphrase.txt'
$repoRoot = $PSScriptRoot
$repoBackupDirectory = Join-Path $repoRoot 'db-backups'
$keepCount = 30
$serviceName = 'MoneyFlow'
$installedExecutable = Join-Path $env:ProgramFiles 'PADS\MoneyFlow\PADS.MoneyFlow.Api.exe'
$temporaryEncryptedBackup = $null

Write-Host 'MoneyFlow DB atsarginė kopija' -ForegroundColor Green
Write-Host "Duomenų bazė: $liveDatabase"
Write-Host "Kopijos:      $backupsDirectory"

if (-not (Test-Path -LiteralPath $liveDatabase -PathType Leaf)) {
    Stop-WithError "Nerasta duomenų bazė: $liveDatabase"
}

$serviceAlive = $false
try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 3
    $serviceAlive = $health.status -eq 'ok'
} catch { }

$backupPath = $null
if ($serviceAlive) {
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        $fileKey = $null
        if (Test-Path -LiteralPath $apiKeyFile -PathType Leaf) {
            $fileKey = (Get-Content -LiteralPath $apiKeyFile -Raw).Trim()
        }

        if ($NoPause) {
            $ApiKey = $fileKey
        } else {
            $entered = Read-Host 'Įveskite API raktą (Enter – naudoti serverio Configuration\api-key.txt)'
            $ApiKey = if ([string]::IsNullOrWhiteSpace($entered)) { $fileKey } else { $entered.Trim() }
        }
    }
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        Stop-WithError 'API raktas nesukonfigūruotas.'
    }

    Write-Step 'Vientisos kopijos kūrimas per programos API'
    try {
        $result = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/maintenance/db-backup" `
            -Headers @{ 'X-Api-Key' = $ApiKey } -TimeoutSec 120
    } catch {
        $statusCode = 0
        if ($_.Exception.Response) { $statusCode = [int]$_.Exception.Response.StatusCode }
        switch ($statusCode) {
            401 { Stop-WithError 'Neteisingas API raktas.' }
            503 { Stop-WithError 'API raktas serveryje nesukonfigūruotas.' }
            default { Stop-WithError "Backup API nepavyko (HTTP $statusCode): $($_.Exception.Message)" }
        }
    }
    $backupPath = $result.fullPath
    if (-not $result.backedUp -or -not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        Stop-WithError 'Backup API negrąžino egzistuojančios kopijos.'
    }
    Write-Host "[GERAI] Sukurta ir patikrinta: $($result.fileName)" -ForegroundColor Green
} else {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-WithError "Programa HTTP neatsako, bet servisas '$serviceName' yra $($service.Status). Tiesioginė aktyvios DB kopija uždrausta."
    }
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath ($liveDatabase + $suffix)) {
            Stop-WithError "Šalia neveikiančios DB rastas '$suffix'. Paleiskite servisą ir kurkite kopiją per API."
        }
    }

    try {
        $exclusive = [IO.File]::Open($liveDatabase, 'Open', 'Read', 'None')
        $exclusive.Dispose()
    } catch {
        Stop-WithError 'DB failą naudoja kitas procesas; tiesioginė kopija būtų nesaugi.'
    }

    Write-Step 'Sustabdytos ir neužrakintos DB kopijavimas'
    New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $backupsDirectory "monthly-money-flow-backup-$timestamp.db"
    Copy-Item -LiteralPath $liveDatabase -Destination $backupPath
}

$oldBackups = @(Get-ChildItem -LiteralPath $backupsDirectory -Filter 'monthly-money-flow-backup-*.db' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip $keepCount)
foreach ($oldBackup in $oldBackups) {
    Remove-Item -LiteralPath $oldBackup.FullName -Force -ErrorAction SilentlyContinue
}

if (-not $SkipGitHub) {
    Write-Step 'Šifruotos kopijos siuntimas į GitHub'
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        Stop-WithError "Nerastas įdiegtos programos DB šifravimo įrankis: $installedExecutable"
    }
    if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
        Stop-WithError 'Nerastas Git. Naudokite -SkipGitHub tik sąmoningai palikdami kopiją lokaliai.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
        Stop-WithError 'Projekto katalogas nėra Git clone. ZIP diegimas negali siųsti backup į GitHub.'
    }
    if (-not (& git -C $repoRoot config user.name) -or -not (& git -C $repoRoot config user.email)) {
        Stop-WithError 'Git user.name ir user.email nesukonfigūruoti šiam serveriui.'
    }

    $passphrase = $env:MONEY_FLOW_BACKUP_PASSPHRASE
    if ([string]::IsNullOrWhiteSpace($passphrase) -and (Test-Path -LiteralPath $passphraseFile -PathType Leaf)) {
        $passphrase = (Get-Content -LiteralPath $passphraseFile -Raw).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($passphrase) -and -not $NoPause) {
        $passphrase = Get-PlainText (Read-Host 'Įveskite backup šifravimo frazę' -AsSecureString)
    }
    if ([string]::IsNullOrWhiteSpace($passphrase) -or $passphrase.Length -lt 20) {
        Stop-WithError 'Backup šifravimo frazė nesukonfigūruota arba trumpesnė nei 20 simbolių.'
    }

    & git -C $repoRoot pull --ff-only --quiet
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Git pull --ff-only nepavyko; siuntimas nutrauktas.' }

    $temporaryEncryptedBackup = Join-Path $env:TEMP ("MoneyFlow-{0}.mfbackup" -f [guid]::NewGuid().ToString('N'))
    $previousPassphrase = $env:MONEY_FLOW_BACKUP_PASSPHRASE
    try {
        $env:MONEY_FLOW_BACKUP_PASSPHRASE = $passphrase
        & $installedExecutable --encrypt-db $backupPath $temporaryEncryptedBackup
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporaryEncryptedBackup -PathType Leaf)) {
            Stop-WithError 'DB kopijos šifravimas nepavyko.'
        }
    } finally {
        if ($null -eq $previousPassphrase) { Remove-Item Env:MONEY_FLOW_BACKUP_PASSPHRASE -ErrorAction SilentlyContinue }
        else { $env:MONEY_FLOW_BACKUP_PASSPHRASE = $previousPassphrase }
        $passphrase = $null
    }

    New-Item -ItemType Directory -Path $repoBackupDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $repoBackupDirectory -File -ErrorAction SilentlyContinue |
        Where-Object Name -Like 'monthly-money-flow-latest.*' |
        Remove-Item -Force
    $repoBackup = Join-Path $repoBackupDirectory 'monthly-money-flow-latest.mfbackup'
    Copy-Item -LiteralPath $temporaryEncryptedBackup -Destination $repoBackup

    $changes = & git -C $repoRoot status --porcelain -- db-backups
    if ($changes) {
        $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        & git -C $repoRoot add -A -- db-backups
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'Nepavyko paruošti backup Git commit.' }
        & git -C $repoRoot commit --quiet -m "Encrypted DB backup $timestamp" -- db-backups
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'Nepavyko sukurti backup Git commit.' }
        & git -C $repoRoot push --quiet origin HEAD
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'GitHub push nepavyko. Kopija liko lokaliai, procesas pažymėtas klaida.' }
    }
    Write-Host '[GERAI] Šifruota kopija išsiųsta į GitHub.' -ForegroundColor Green
}

if ($temporaryEncryptedBackup) {
    Remove-Item -LiteralPath $temporaryEncryptedBackup -Force -ErrorAction SilentlyContinue
}
Write-Host "`nAtsarginė kopija baigta: $backupPath" -ForegroundColor Green
Wait-Exit 0
