# Windows LAN diegimas

## Paskirtis ir ribos

Programa skirta vienam kontroliuojamam Windows LAN serveriui. Numatytasis adresas yra `http://0.0.0.0:5000`; ugniasienė atidaro TCP 5000 tik Domain ir Private profiliams, kad skaitymo UI pasiektų visi vidinio tinklo naudotojai. Interneto prieigos nesuteikite. Skaitymo `GET/HEAD` užklausos yra atviros, o kiekvienai `/api` mutacijai (`POST`, `PUT`, `PATCH`, `DELETE`) privalomas `X-Api-Key`.

## Pirmas diegimas

1. Įdiekite .NET 10 SDK.
2. Klonuokite privatų repo. ZIP netinka, jei backup turi būti siunčiamas į GitHub, nes neturi `.git` metaduomenų.
3. Backup serverio Git paskyrai nustatykite `user.name`, `user.email` ir autentifikaciją su push teise į privatų repo. Prieš suteikdami naujam serveriui repo prieigą patvirtinkite, kad Git istorijoje nebėra senų nešifruotų DB objektų.
4. Dukart spustelėkite `Deploy-MoneyFlow.bat`. Paleidiklis pats paprašys administratoriaus teisių ir, jei tai pirmas diegimas, parinks `db-backups\monthly-money-flow-latest.mfbackup`.

5. Scenarijus paruošia ir patikrina `win-x64` paketą dar veikiant senai versijai, tada trumpam pakeičia programos katalogą. Nauja versija laikoma sėkminga tik kai `/ready` patvirtina DB; kitu atveju automatiškai grąžinamas ankstesnis katalogas. Servisas turi veikti kaip `NT SERVICE\MoneyFlow`, ne `LocalSystem`.
6. Servisas veikia kaip virtuali `NT SERVICE\MoneyFlow` paskyra, turinti keitimo teises tik `ProgramData` duomenims ir skaitymo / vykdymo teises programai.
7. Vietinis operatorius iš `C:\Program Files\PADS\MoneyFlow` paleidžia `Set-MoneyFlowApiKey.bat`, tada `Set-MoneyFlowBackupPassphrase.bat`.

Pirmo diegimo metu `Deploy-MoneyFlow.bat` automatiškai naudoja repo esančią šifruotą GitHub kopiją. Scenarijus paprašo šifravimo frazės, kopiją iššifruoja tik laikiname faile ir prieš diegdamas vykdo `PRAGMA integrity_check`. Egzistuojanti gyva DB neperrašoma.

## Atnaujinimas ir rollback

Atnaujinę repo dar kartą paleiskite `Deploy-MoneyFlow.bat`. Esama gyva DB aptinkama automatiškai ir neperrašoma. Publish arba staging klaida seno serviso nestabdo. Prieš pirmą naujos versijos startą sukuriama `pre-deploy-*` DB kopija. Aktyvavimo ar readiness klaida sustabdo naują versiją, grąžina ankstesnį programos katalogą bei DB ir paleidžia ankstesnę versiją.

## Atsarginė kopija

`Backup-MoneyFlowDb.bat` veikiančiai paslaugai nereikalauja administratoriaus teisių:

1. Offline DB įrankis sukuria vientisą SQLite kopiją tiesiai iš `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db`; API raktas nereikalingas.
2. Kopija iškart patikrinama per `integrity_check`.
3. Operatorius AES-256-GCM formatu užšifruoja kopiją į `monthly-money-flow-latest.mfbackup`.
4. Vykdomas `git pull --ff-only`, commit ir push. Šifravimo ar Git klaida grąžina nesėkmės kodą.

Skriptas nebesiremia HTTP `/health` ar `/api/maintenance/db-backup`, todėl backup procesui nereikia nei veikiančio web endpoint’o, nei mutacijų API rakto. HTTP maintenance endpoint’as paliktas kaip apsaugotas pagalbinis kelias rankiniams/diagnostiniams atvejams.

## Atkūrimas

`Restore-MoneyFlowDb.bat`:

1. Administratoriaus teisių neprašo; operaciją saugo mutacijų API raktas.
2. Leidžia pasirinkti `.mfbackup` arba vientisą `.db` kopiją.
3. Nusiunčia kopiją veikiančiam servisui, kuris ją iššifruoja ir vykdo `PRAGMA integrity_check`.
4. Servisas trumpam nebepriima naujų DB užklausų ir palaukia, kol aktyvios užklausos baigsis.
5. Per SQLite backup API sukuria `pre-restore-*` rollback kopiją ir atkuria DB nestabdydamas proceso.
6. Tikrina `/ready`; nesėkmės atveju tame pačiame procese automatiškai grąžina rollback kopiją.

Atkūrimo metu UI gali likti atidarytas. Kelias sekundes DB užklausos gali grąžinti `503 Database maintenance is in progress`; jas galima pakartoti pasibaigus atkūrimui.

## Backup šifravimo frazė

- Turi būti bent 20 simbolių ir nesutapti su mutacijų API raktu.
- Serveryje saugoma `C:\ProgramData\PADS\MoneyFlow\Configuration\backup-passphrase.txt`.
- Git saugomas tik `.mfbackup`; frazę perduokite atkūrimo administratoriui atskiru patvirtintu kanalu.
- Praradus frazę kopijos neatkuriamos, todėl laikykite ją įmonės slaptažodžių saugykloje.
- Senos nešifruotos DB versijos Git istorijoje savaime nedingsta. Joms pašalinti reikia suderinto istorijos perrašymo ir visų clone atnaujinimo.

## Tinklas ir reverse proxy

- Serveriui skirkite DHCP rezervaciją arba statinį LAN IP.
- Pagal nutylėjimą `RemoteAddress=Any` taikomas tik Domain/Private profiliams, todėl UI pasiekia visi vidinio tinklo klientai. Jei įmonės politika reikalauja siauresnės ribos, pakeiskite jį į reikalingus VLAN / potinklius.
- Rekomenduojama IIS, Caddy ar Nginx priimti vidinį DNS vardą ir proxy perduoti į `http://localhost:5000`.
- Po proxy patikros uždarykite tiesioginę 5000 prieigą klientams, jei ji nebereikalinga.
- HTTPS ir vidinio sertifikato pasitikėjimą tvarko tinklo administratorius.

## Failų teisės

Diegimo scenarijus ACL pritaiko automatiškai. `NT SERVICE\MoneyFlow` gali keisti DB ir backup katalogą. Paprasti vietiniai naudotojai gali keisti tik `Configuration` failus ir skaityti API sukurtas kopijas jų šifravimui; gyvos DB skaitymo teisės jiems nesuteikiamos.

## Diagnostika

```powershell
Get-Service MoneyFlow
Get-CimInstance Win32_Service -Filter "Name='MoneyFlow'" | Select-Object Name, StartName, State
Invoke-RestMethod http://localhost:5000/health
Invoke-RestMethod http://localhost:5000/ready
Get-NetFirewallRule -DisplayName 'MoneyFlow LAN (TCP 5000)' | Get-NetFirewallAddressFilter
```

Papildomai: [troubleshooting.md](troubleshooting.md).
