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
- 503: raktas serveryje dar nesukonfigūruotas; paleiskite `Set-MoneyFlowApiKey.ps1`.
- 413: JSON viršijo 10 MB ribą; patikrinkite eksportą, o ne aklai didinkite limitą.
- 400: peržiūrėkite API klaidos tekstą ir JSON schemos versiją / privalomus laukus.

Neloginkite ir nesiųskite viso tikro JSON į viešą issue.

## DB užrakinta arba sugadinta

Pirmiausia sustabdykite paslaugą ir pasidarykite bitinę failų kopiją už repo. Nešalinkite `-wal` / `-shm` failų aklai. Atkūrimą atlikite iš patikrintos atsarginės kopijos ir po to paleiskite integracines patikras su nuasmeninta kopija atskiroje aplinkoje.

Automatiniam atkūrimui naudokite `Restore-MoneyFlowDb.ps1`: jis prieš keitimą tikrina `integrity_check`, o po keitimo `/ready`; nesėkmės atveju grąžina ankstesnę DB.

Jei `Backup-MoneyFlowDb.ps1` šifravimo metu rodo `attempt to write a readonly database`, naudojama sena backup eiga arba pasenęs įdiegtas exe. Atnaujinta eiga nebekviečia web aplikacijos šifravimui ir kopiją kuria per `--backup-db`, todėl backup’ui nebereikia importų API rakto.

## UI rodo seną versiją

HTML siunčiamas su `no-cache`; atlikite hard refresh ir patikrinkite, ar serverio `wwwroot` tikrai buvo pakeistas naujausiu publish rezultatu. CDN šiai LAN programai nerekomenduojamas.
