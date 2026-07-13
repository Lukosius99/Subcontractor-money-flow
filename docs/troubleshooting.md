# Trikčių šalinimas

## Paslauga nepasileidžia

```powershell
Get-Service MoneyFlow
Get-WinEvent -LogName Application -MaxEvents 50 |
  Where-Object ProviderName -in '.NET Runtime','Application Error' |
  Format-List TimeCreated,ProviderName,Message
```

Patikrinkite .NET 10, `ProgramData` teises, DB disko vietą ir `appsettings.Production.json` kelią.

## Vietoje veikia, LAN neveikia

```powershell
Invoke-WebRequest http://localhost:5000/health
Invoke-WebRequest http://localhost:5000/ready
Get-NetConnectionProfile
Get-NetFirewallRule -DisplayName "MoneyFlow LAN (TCP 5000)"
Test-NetConnection SERVERIO-IP -Port 5000
```

Serverio profilis turi būti DomainAuthenticated arba Private. Jei IP veikia, o vardas ne — tikrinkite vidinį DNS.

## 5000 prievadas užimtas

```powershell
Get-NetTCPConnection -LocalPort 5000 -State Listen
Get-Process -Id (Get-NetTCPConnection -LocalPort 5000 -State Listen).OwningProcess
```

## Importas grąžina 401 arba 503

- 401: PAD `X-Api-Key` nesutampa su serverio raktu.
- 503: raktas serveryje dar nesukonfigūruotas; paleiskite `Set-MoneyFlowApiKey.bat`. Atkūrimo metu trumpas 503 taip pat reiškia suplanuotą DB priežiūros langą.
- 413: JSON viršijo 10 MB ribą; patikrinkite eksportą, o ne aklai didinkite limitą.
- 400: peržiūrėkite API klaidos tekstą ir JSON schemos versiją / privalomus laukus.

## Redagavimas grąžina 401 arba 503

- 401: įvesta redagavimo slaptafrazė nesutampa su serveryje nustatyta reikšme. API raktas čia netinka.
- 503: redagavimo slaptafrazė dar nenustatyta; serveryje paleiskite `Set-MoneyFlowEditPassphrase.bat`. Serviso perkrauti nereikia.

Neloginkite ir nesiųskite viso tikro JSON į viešą issue.

## DB atkūrimas

Įprastam atkūrimui paleiskite `Restore-MoneyFlowDb.bat`. Administratoriaus teisių nereikia, servisas nestabdomas: API patikrina kopiją, sukuria `pre-restore-*` rollback failą, atkuria SQLite turinį ir patikrina `/ready`.

Jei servisas visai nepasileidžia arba gyva DB tiek sugadinta, kad nepavyksta sukurti rollback kopijos, online atkūrimas sąmoningai nevykdomas. Tokiu avariniu atveju administratorius turi sustabdyti paslaugą ir išsaugoti DB su `-wal` / `-shm` failais prieš rankinį keitimą. Šių failų aklai nešalinkite.

Jei `Backup-MoneyFlowDb.bat` grąžina 401, patikrinkite `C:\ProgramData\PADS\MoneyFlow\Configuration\api-key.txt` ir serverio API raktą. Jei rodoma `unable to open database file`, naudojama sena tiesioginio DB skaitymo backup eiga — atnaujinkite repo ir dar kartą paleiskite `Deploy-MoneyFlow.bat`. Dabartinė eiga kopiją kuria per serviso `/api/maintenance/db-backup` endpointą.

## UI rodo seną versiją

HTML siunčiamas su `no-cache`; atlikite hard refresh ir patikrinkite, ar serverio `wwwroot` tikrai buvo pakeistas naujausiu publish rezultatu. CDN šiai LAN programai nerekomenduojamas.
