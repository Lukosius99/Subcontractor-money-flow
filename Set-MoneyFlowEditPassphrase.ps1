[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$configurationDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow\Configuration'
$passphraseFile = Join-Path $configurationDirectory 'edit-passphrase.txt'
$apiKeyFile = Join-Path $configurationDirectory 'api-key.txt'
$temporaryFile = Join-Path $configurationDirectory ("edit-passphrase-{0}.tmp" -f [guid]::NewGuid().ToString('N'))
$iterations = 310000
$passphrase = $null
$hash = $null

try {
    if (-not (Test-Path -LiteralPath $configurationDirectory)) {
        throw "Nerastas $configurationDirectory. Pirmiausia paleiskite Deploy-MoneyFlow.bat."
    }

    $secureValue = Read-Host 'Įveskite bent 16 simbolių redagavimo slaptafrazę' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try { $passphrase = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }

    if ([string]::IsNullOrWhiteSpace($passphrase) -or $passphrase.Length -lt 16) {
        throw 'Redagavimo slaptafrazė turi būti bent 16 simbolių.'
    }
    if ($passphrase.Contains("`r") -or $passphrase.Contains("`n")) {
        throw 'Redagavimo slaptafrazė negali turėti naujos eilutės simbolių.'
    }
    if (Test-Path -LiteralPath $apiKeyFile -PathType Leaf) {
        $apiKey = (Get-Content -LiteralPath $apiKeyFile -Raw).Trim()
        if ($passphrase -ceq $apiKey) {
            throw 'Redagavimo slaptafrazė turi skirtis nuo PAD / maintenance API rakto.'
        }
        $apiKey = $null
    }

    $salt = New-Object byte[] 16
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($salt) }
    finally { $random.Dispose() }

    $derive = [Security.Cryptography.Rfc2898DeriveBytes]::new(
        $passphrase,
        $salt,
        $iterations,
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $hash = $derive.GetBytes(32) }
    finally { $derive.Dispose() }

    $encoded = 'PBKDF2-SHA256${0}${1}${2}' -f `
        $iterations,
        [Convert]::ToBase64String($salt),
        [Convert]::ToBase64String($hash)
    [IO.File]::WriteAllText($temporaryFile, $encoded, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryFile -Destination $passphraseFile -Force

    Write-Host "`n[GERAI] Redagavimo slaptafrazė išsaugota kaip PBKDF2 maišas." -ForegroundColor Green
    Write-Host 'Paslaugos perkrauti nereikia. Nauja slaptafrazė galios nuo kitos redagavimo užklausos.'
}
catch {
    Write-Host "`nKLAIDA: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    $passphrase = $null
    if ($hash) { [Array]::Clear($hash, 0, $hash.Length) }
    Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
}
