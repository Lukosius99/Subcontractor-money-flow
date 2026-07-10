# Windows LAN diegimas

## Paskirtis ir ribos

Programa skirta vienam kontroliuojamam Windows LAN serveriui. Numatytasis adresas yra `http://0.0.0.0:5000`; ugniasienė atidaro TCP 5000 tik Domain ir Private profiliams. Interneto prieigos nesuteikite.

## Pirmas diegimas

1. Įdiekite .NET 10 SDK.
2. Klonuokite privatų repo. ZIP netinka, jei backup turi būti siunčiamas į GitHub, nes neturi `.git` metaduomenų.
3. Backup serverio Git paskyrai nustatykite `user.name`, `user.email` ir autentifikaciją su push teise į privatų repo.
4. Administratoriaus PowerShell lange vykdykite:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
   ```

5. Scenarijus paruošia ir patikrina `win-x64` paketą dar veikiant senai versijai, tada trumpam pakeičia programos katalogą. Nauja versija laikoma sėkminga tik kai `/ready` patvirtina DB; kitu atveju automatiškai grąžinamas ankstesnis katalogas.
6. Servisas veikia kaip virtuali `NT SERVICE\MoneyFlow` paskyra, turinti keitimo teises tik `ProgramData` duomenims ir skaitymo / vykdymo teises programai.
7. Vietinis operatorius paleidžia abu konfigūravimo scenarijus:

   ```powershell
   & 'C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowApiKey.ps1'
   & 'C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowBackupPassphrase.ps1'
   ```

Jei pirmo diegimo metu reikia atkurti šifruotą GitHub kopiją:

```powershell
$env:MONEY_FLOW_BACKUP_PASSPHRASE = '<atskiru kanalu gauta frazė>'
.\Deploy-MoneyFlow.ps1 -InitialDatabase '.\db-backups\monthly-money-flow-latest.mfbackup'
```

Scenarijus kopiją iššifruoja tik laikiname faile ir prieš diegdamas vykdo `PRAGMA integrity_check`. Nešifruota išorinė `.db` taip pat palaikoma, bet turi būti uždaryta ir be `-wal`, `-shm` ar `-journal`. Egzistuojanti gyva DB neperrašoma.

## Atnaujinimas ir rollback

Dar kartą paleiskite `Deploy-MoneyFlow.ps1` be `-InitialDatabase`. Publish arba staging klaida seno serviso nestabdo. Prieš pirmą naujos versijos startą sukuriama `pre-deploy-*` DB kopija. Aktyvavimo ar readiness klaida sustabdo naują versiją, grąžina ankstesnį programos katalogą bei DB ir paleidžia ankstesnę versiją.

## Atsarginė kopija

`Backup-MoneyFlowDb.bat` veikiančiai paslaugai nereikalauja administratoriaus teisių:

1. `POST /api/maintenance/db-backup` sukuria `VACUUM INTO` kopiją.
2. API iškart vykdo kopijos `integrity_check`.
3. Operatorius AES-256-GCM formatu užšifruoja kopiją į `monthly-money-flow-latest.mfbackup`.
4. Vykdomas `git pull --ff-only`, commit ir push. Šifravimo ar Git klaida grąžina nesėkmės kodą.

Jei HTTP neatsako, aktyvios DB kopijuoti neleidžiama. Tiesioginė kopija galima tik kai servisas patvirtintai sustabdytas, nėra SQLite sidecar failų ir DB galima atidaryti išskirtinai.

## Atkūrimas

`Restore-MoneyFlowDb.bat`:

1. Prašo administratoriaus teisių.
2. Iššifruoja `.mfbackup` į laikiną failą.
3. Prieš keitimą vykdo `PRAGMA integrity_check`.
4. Sustabdo servisą ir sukuria `pre-restore-*` dabartinės DB kopiją.
5. Pakeičia DB, paleidžia servisą ir tikrina `/ready`.
6. Nesėkmės atveju automatiškai grąžina `pre-restore-*` DB ir grąžina klaidos kodą.

## Backup šifravimo frazė

- Turi būti bent 20 simbolių ir nesutapti su importų API raktu.
- Serveryje saugoma `C:\ProgramData\PADS\MoneyFlow\Configuration\backup-passphrase.txt`.
- Git saugomas tik `.mfbackup`; frazę perduokite atkūrimo administratoriui atskiru patvirtintu kanalu.
- Praradus frazę kopijos neatkuriamos, todėl laikykite ją įmonės slaptažodžių saugykloje.
- Senos nešifruotos DB versijos Git istorijoje savaime nedingsta. Joms pašalinti reikia suderinto istorijos perrašymo ir visų clone atnaujinimo.

## Tinklas ir reverse proxy

- Serveriui skirkite DHCP rezervaciją arba statinį LAN IP.
- Apribokite ugniasienės `RemoteAddress` iki reikalingo VLAN / potinklio.
- Rekomenduojama IIS, Caddy ar Nginx priimti vidinį DNS vardą ir proxy perduoti į `http://localhost:5000`.
- Po proxy patikros uždarykite tiesioginę 5000 prieigą klientams, jei ji nebereikalinga.
- HTTPS ir vidinio sertifikato pasitikėjimą tvarko tinklo administratorius.

## Failų teisės

Diegimo scenarijus ACL pritaiko automatiškai. `NT SERVICE\MoneyFlow` gali keisti DB ir backup katalogą. Paprasti vietiniai naudotojai gali keisti tik `Configuration` failus ir skaityti API sukurtas kopijas jų šifravimui; gyvos DB skaitymo teisės jiems nesuteikiamos.

## Diagnostika

```powershell
Get-Service MoneyFlow
Invoke-RestMethod http://localhost:5000/health
Invoke-RestMethod http://localhost:5000/ready
```

Papildomai: [troubleshooting.md](troubleshooting.md).
