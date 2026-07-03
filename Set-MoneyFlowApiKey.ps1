[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$configurationDirectory = Join-Path $env:ProgramData 'PADS\MoneyFlow\Configuration'
$keyFile = Join-Path $configurationDirectory 'api-key.txt'
$temporaryFile = Join-Path $configurationDirectory ("api-key-{0}.tmp" -f [guid]::NewGuid().ToString('N'))

try {
    if (-not (Test-Path -LiteralPath $configurationDirectory)) {
        throw "Nerastas $configurationDirectory. Administratorius pirmiausia turi paleisti naujausią Deploy-MoneyFlow.ps1."
    }

    $secureKey = Read-Host 'Įveskite PAD / Cloud Flow API raktą (simboliai nebus rodomi)' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    try { $apiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }

    if ([string]::IsNullOrWhiteSpace($apiKey)) { throw 'API raktas negali būti tuščias.' }
    if ($apiKey.Length -lt 16) { throw 'API raktas per trumpas. Patikrinkite, ar įklijavote visą reikšmę.' }
    if ($apiKey.Contains("`r") -or $apiKey.Contains("`n")) { throw 'API raktas negali turėti naujos eilutės simbolių.' }

    [IO.File]::WriteAllText($temporaryFile, $apiKey, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryFile -Destination $keyFile -Force

    Write-Host "`n[GERAI] MoneyFlow API raktas išsaugotas." -ForegroundColor Green
    Write-Host 'Paslaugos perkrauti nereikia. Naujas raktas bus naudojamas nuo kitos importo užklausos.'
    Write-Host 'Naudokite tą pačią reikšmę PAD / Cloud Flow X-Api-Key antraštėje.'
}
catch {
    Write-Host "`nKLAIDA: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
}
