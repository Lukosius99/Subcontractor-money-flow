# Windows LAN diegimas

## Paskirtis ir ribos

Programa skirta vienam kontroliuojamam Windows LAN serveriui. Numatytasis adresas yra `http://0.0.0.0:5000`; ugniasienė atidaro TCP 5000 tik Domain ir Private profiliams, kad skaitymo UI pasiektų visi vidinio tinklo naudotojai. Interneto prieigos nesuteikite. Skaitymo `GET/HEAD` užklausos yra atviros. Rankinius Web UI pakeitimus saugo redagavimo slaptafrazė, o PAD importus ir DB priežiūros endpointus – `X-Api-Key`.

## Pirmas diegimas

1. Įdiekite Git for Windows ir .NET 10 SDK.
2. Klonuokite privatų repo. ZIP netinka, jei backup turi būti siunčiamas į GitHub, nes neturi `.git` metaduomenų.
3. Backup serverio Git paskyrai nustatykite `user.name`, `user.email` ir autentifikaciją su push teise į privatų repo. Prieš suteikdami naujam serveriui repo prieigą patvirtinkite, kad Git istorijoje nebėra senų nešifruotų DB objektų.
4. Tiesioginei LAN prieigai per `:5000` dukart spustelėkite `Deploy-MoneyFlow.bat`. Tame pačiame serveryje veikiančiam reverse proxy naudokite `Deploy-MoneyFlow-ReverseProxy.bat`. Paleidiklis pats paprašys administratoriaus teisių ir, jei tai pirmas diegimas, parinks `db-backups\monthly-money-flow-latest.mfbackup`.

5. Scenarijus paruošia ir patikrina `win-x64` paketą dar veikiant senai versijai, tada trumpam pakeičia programos katalogą. Nauja versija laikoma sėkminga tik kai `/ready` patvirtina DB; kitu atveju automatiškai grąžinamas ankstesnis katalogas. Servisas turi veikti kaip `NT SERVICE\MoneyFlow`, ne `LocalSystem`.
6. Servisas veikia kaip virtuali `NT SERVICE\MoneyFlow` paskyra, turinti keitimo teises tik `ProgramData` duomenims ir skaitymo / vykdymo teises programai.
7. Vietinis operatorius iš `C:\Program Files\PADS\MoneyFlow` paleidžia `Set-MoneyFlowApiKey.bat`, `Set-MoneyFlowEditPassphrase.bat` ir `Set-MoneyFlowBackupPassphrase.bat`.

Pirmo diegimo metu `Deploy-MoneyFlow.bat` automatiškai naudoja repo esančią šifruotą GitHub kopiją. Scenarijus paprašo šifravimo frazės, kopiją iššifruoja tik laikiname faile ir prieš diegdamas vykdo `PRAGMA integrity_check`. Egzistuojanti gyva DB neperrašoma.

## Atnaujinimas ir rollback

Atnaujinę repo dar kartą paleiskite `Deploy-MoneyFlow.bat`. Pirmo diegimo metu pasirinktas `DirectLan` arba `ReverseProxy` režimas saugomas `ProgramData` ir vėlesniu atnaujinimu neperrašomas. Esama gyva DB aptinkama automatiškai ir neperrašoma. Publish arba staging klaida seno serviso nestabdo. Prieš pirmą naujos versijos startą sukuriama `pre-deploy-*` DB kopija. Aktyvavimo ar readiness klaida sustabdo naują versiją, grąžina ankstesnį programos katalogą bei DB ir paleidžia ankstesnę versiją.

## Atsarginė kopija

`Backup-MoneyFlowDb.bat` veikiančiai paslaugai nereikalauja administratoriaus teisių:

1. Skriptas perskaito production API raktą iš `Configuration\api-key.txt` ir kreipiasi į `POST /api/maintenance/db-backup`.
2. Servisas sukuria vientisą SQLite kopiją neteikdamas operatoriui gyvos DB skaitymo teisių.
3. Kopija iškart patikrinama per `integrity_check`.
4. Operatorius AES-256-GCM formatu užšifruoja kopiją į `monthly-money-flow-latest.mfbackup`.
5. Vykdomas `git pull --ff-only`, commit ir push. Šifravimo ar Git klaida grąžina nesėkmės kodą.

API raktas būtinas, tačiau administratoriaus teisės ir serviso stabdymas nereikalingi. Tai išlaiko mažiausių teisių modelį: paprasti naudotojai negali tiesiogiai skaityti gyvos DB.

## Atkūrimas

`Restore-MoneyFlowDb.bat`:

1. Administratoriaus teisių neprašo; operaciją saugo API raktas.
2. Leidžia pasirinkti `.mfbackup` arba vientisą `.db` kopiją.
3. Nusiunčia kopiją veikiančiam servisui, kuris ją iššifruoja ir vykdo `PRAGMA integrity_check`.
4. Servisas trumpam nebepriima naujų DB užklausų ir palaukia, kol aktyvios užklausos baigsis.
5. Per SQLite backup API sukuria `pre-restore-*` rollback kopiją ir atkuria DB nestabdydamas proceso.
6. Tikrina `/ready`; nesėkmės atveju tame pačiame procese automatiškai grąžina rollback kopiją.

Atkūrimo metu UI gali likti atidarytas. Kelias sekundes DB užklausos gali grąžinti `503 Database maintenance is in progress`; jas galima pakartoti pasibaigus atkūrimui.

## Backup šifravimo frazė

- Turi būti bent 20 simbolių ir nesutapti su API raktu ar redagavimo slaptafraze.
- Serveryje saugoma `C:\ProgramData\PADS\MoneyFlow\Configuration\backup-passphrase.txt`.
- Git saugomas tik `.mfbackup`; frazę perduokite atkūrimo administratoriui atskiru patvirtintu kanalu.
- Praradus frazę kopijos neatkuriamos, todėl laikykite ją įmonės slaptažodžių saugykloje.
- Senos nešifruotos DB versijos Git istorijoje savaime nedingsta. Joms pašalinti reikia suderinto istorijos perrašymo ir visų clone atnaujinimo.

## Redagavimo slaptafrazė

- Nustatoma paleidus `Set-MoneyFlowEditPassphrase.bat`; administratoriaus teisių ir serviso perkrovimo nereikia.
- Turi būti bent 16 simbolių ir nesutapti su API raktu.
- Serveryje saugomas tik PBKDF2-SHA256 hash failas `C:\ProgramData\PADS\MoneyFlow\Configuration\edit-passphrase.txt`, ne pati slaptafrazė.
- Naudotojo naršyklė slaptafrazę laiko tik atverto puslapio atmintyje. Uždarius ar perkrovus puslapį ją reikia įvesti iš naujo.

## Tinklas ir reverse proxy

- Serveriui skirkite DHCP rezervaciją arba statinį LAN IP.
- Pagal nutylėjimą `RemoteAddress=Any` taikomas tik Domain/Private profiliams, todėl UI pasiekia visi vidinio tinklo klientai. Jei įmonės politika reikalauja siauresnės ribos, pakeiskite jį į reikalingus VLAN / potinklius.
- `DirectLan` režime vidinis DNS vardas veda į serverio IP, o naudotojai jungiasi per `http://vardas:5000`.
- Adresui be prievado tame pačiame serveryje paleiskite `Deploy-MoneyFlow-ReverseProxy.bat`, o IIS, Caddy ar Nginx TCP 80 perduokite į `http://127.0.0.1:5000`. Šiuo režimu MoneyFlow neatidaro TCP 5000 LAN ugniasienėje.
- Jei reverse proxy yra kitame serveryje, naudokite `DirectLan` ir jo IP apribokite MoneyFlow ugniasienės `RemoteAddress` taisykle.
- Šiame projekte leidžiamas tik vidinis HTTP. API raktas ir redagavimo slaptafrazė tinkle nešifruojami, todėl serverio nepublikuokite internete ir laikykite patikimame VLAN.

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
