[CmdletBinding()]
param(
    # Backwards compatibility only: backup no longer uses the HTTP API.
    [string]$ApiKey,
    # Backwards compatibility only: backup no longer uses the HTTP API.
    [string]$BaseUrl,
    [string]$DatabasePath,
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
function Get-DotnetSdk {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnet) { $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue }
    if (-not $dotnet) { return $null }

    $sdks = @(& $dotnet.Source --list-sdks 2>$null)
    if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^10\.' })) {
        return $dotnet.Source
    }

    return $null
}
function Resolve-DatabaseTool {
    if ($script:DatabaseToolExecutable) { return $script:DatabaseToolExecutable }

    $projectFile = Join-Path $repoRoot 'PADS.MoneyFlow.Api.csproj'
    $dotnet = Get-DotnetSdk
    if ($dotnet -and (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        Write-Step 'DB įrankio paruošimas iš repo kodo'
        $buildOutput = & $dotnet build $projectFile -c Release --nologo 2>&1
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            $buildOutput | ForEach-Object { Write-Host $_ }
            Stop-WithError "DB įrankio build nepavyko (dotnet build grąžino $buildExitCode)."
        }

        $builtExecutable = Join-Path $repoRoot 'bin\Release\net10.0\PADS.MoneyFlow.Api.exe'
        if (-not (Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
            Stop-WithError "Po build nerastas DB įrankis: $builtExecutable"
        }

        $script:DatabaseToolExecutable = $builtExecutable
        return $script:DatabaseToolExecutable
    }

    if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
        $script:DatabaseToolExecutable = $installedExecutable
        return $script:DatabaseToolExecutable
    }

    Stop-WithError 'Nerastas .NET 10 SDK ir įdiegtas MoneyFlow exe. Negaliu saugiai sukurti DB kopijos.'
}
function Invoke-DatabaseTool([string[]]$ToolArguments) {
    $tool = Resolve-DatabaseTool
    & $tool @ToolArguments
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError "DB įrankis nepavyko: $tool $($ToolArguments -join ' ')"
    }
}

$dataDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow'
$defaultDatabase = Join-Path $dataDirectory 'monthly-money-flow.db'
$liveDatabase = if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $defaultDatabase
} else {
    [IO.Path]::GetFullPath($DatabasePath)
}
$databaseDirectory = Split-Path -Parent $liveDatabase
$backupsDirectory = Join-Path $databaseDirectory 'Backups'
$passphraseFile = Join-Path $dataDirectory 'Configuration\backup-passphrase.txt'
$repoRoot = $PSScriptRoot
$repoBackupDirectory = Join-Path $repoRoot 'db-backups'
$keepCount = 30
$installedExecutable = Join-Path $env:ProgramFiles 'PADS\MoneyFlow\PADS.MoneyFlow.Api.exe'
$temporaryEncryptedBackup = $null

Write-Host 'MoneyFlow DB atsarginė kopija' -ForegroundColor Green
Write-Host "Duomenų bazė: $liveDatabase"
Write-Host "Kopijos:      $backupsDirectory"

if ($PSBoundParameters.ContainsKey('ApiKey') -or $PSBoundParameters.ContainsKey('BaseUrl')) {
    Write-Host '[INFO] API raktas ir BaseUrl nebenaudojami backup kūrimui; skriptas dirba tiesiogiai su SQLite.' -ForegroundColor Yellow
}

if (-not (Test-Path -LiteralPath $liveDatabase -PathType Leaf)) {
    Stop-WithError "Nerasta duomenų bazė: $liveDatabase"
}

Write-Step 'Vientisos SQLite kopijos kūrimas be API rakto'
New-Item -ItemType Directory -Path $backupsDirectory -Force | Out-Null
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $backupsDirectory "monthly-money-flow-backup-$timestamp.db"
$attempt = 2
while (Test-Path -LiteralPath $backupPath) {
    $backupPath = Join-Path $backupsDirectory "monthly-money-flow-backup-$timestamp-$attempt.db"
    $attempt++
}

Invoke-DatabaseTool @('--backup-db', $liveDatabase, $backupPath)
if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
    Stop-WithError 'DB įrankis negrąžino egzistuojančios kopijos.'
}
Write-Host "[GERAI] Sukurta ir patikrinta: $(Split-Path -Leaf $backupPath)" -ForegroundColor Green

$oldBackups = @(Get-ChildItem -LiteralPath $backupsDirectory -Filter 'monthly-money-flow-backup-*.db' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip $keepCount)
foreach ($oldBackup in $oldBackups) {
    Remove-Item -LiteralPath $oldBackup.FullName -Force -ErrorAction SilentlyContinue
}

if (-not $SkipGitHub) {
    Write-Step 'Šifruotos kopijos siuntimas į GitHub'
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
        Invoke-DatabaseTool @('--encrypt-db', $backupPath, $temporaryEncryptedBackup)
        if (-not (Test-Path -LiteralPath $temporaryEncryptedBackup -PathType Leaf)) {
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
