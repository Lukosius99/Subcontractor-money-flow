[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$configurationDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow\Configuration'
$passphraseFile = Join-Path $configurationDirectory 'backup-passphrase.txt'
$temporaryFile = Join-Path $configurationDirectory ("backup-passphrase-{0}.tmp" -f [guid]::NewGuid().ToString('N'))

try {
    if (-not (Test-Path -LiteralPath $configurationDirectory)) {
        throw "Nerastas $configurationDirectory. Pirmiausia paleiskite Deploy-MoneyFlow.ps1."
    }
    $secureValue = Read-Host 'Įveskite bent 20 simbolių backup šifravimo frazę' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try { $passphrase = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }

    if ([string]::IsNullOrWhiteSpace($passphrase) -or $passphrase.Length -lt 20) {
        throw 'Šifravimo frazė turi būti bent 20 simbolių.'
    }
    if ($passphrase.Contains("`r") -or $passphrase.Contains("`n")) {
        throw 'Šifravimo frazė negali turėti naujos eilutės simbolių.'
    }

    [IO.File]::WriteAllText($temporaryFile, $passphrase, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryFile -Destination $passphraseFile -Force
    Write-Host "`n[GERAI] Backup šifravimo frazė išsaugota." -ForegroundColor Green
    Write-Host 'Tą pačią frazę saugiai perduokite naujo serverio administratoriui; į Git jos nedėkite.'
} catch {
    Write-Host "`nKLAIDA: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    $passphrase = $null
    Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
}
