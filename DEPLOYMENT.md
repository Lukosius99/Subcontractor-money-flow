# MoneyFlow diegimas ir LAN administravimas

Šis aprašas skirtas Windows 11 kompiuterį ir įmonės tinklą prižiūrinčiam administratoriui.

## Kas įdiegiama

- Windows paslaugos vardas: `MoneyFlow`.
- Programos failai: `C:\Program Files\PADS\MoneyFlow`.
- Vidinis Kestrel adresas: `http://0.0.0.0:5000`.
- Gyva SQLite DB: `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db`.
- Užkardos taisyklė: TCP 5000, tik **Domain** ir **Private** profiliams.
- Aplinka paslaugai: `ASPNETCORE_ENVIRONMENT=Production`.

Programa nepriklauso nuo PowerShell esamo aplanko: konfigūracija publikuojama kartu su programa, o gamybinės DB kelias yra absoliutus.

## Pirmas diegimas

1. Privačiame GitHub repo pasirinkite **Code → Download ZIP** arba naudokite `git clone`.
2. Išskleiskite visą ZIP.
3. Įdiekite **.NET 10 SDK**. Scenarijus publikuoja kodą serveryje, todėl vien Runtime nepakanka.
4. Paleiskite `Deploy-MoneyFlow.ps1`. Jis pats paprašys administratoriaus teisių.
5. Įveskite slaptą PAD / Cloud Flow API raktą. Atnaujinant jis išsaugomas paslaugos konfigūracijoje ir dar kartą neprašomas.
6. Scenarijus publikuoja į laikiną aplanką, sustabdo seną paslaugą, pakeičia tik programos failus, įdiegia arba atnaujina paslaugą ir ją paleidžia.
7. Jei gyvos DB nėra, `seed\monthly-money-flow.db` nukopijuojama į `ProgramData`. Jei DB jau yra, ji neperrašoma.
8. Patikrinamas `http://localhost:5000` ir parodomi kompiuterio bei LAN IP adresai.

## Atnaujinimas neprarandant duomenų

Atsisiųskite ir išskleiskite naują repo versiją, tada dar kartą vykdykite:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
```

Gyva DB `C:\ProgramData\PADS\MoneyFlow` lieka nepakeista. Net jei naujame ZIP yra naujesnė seed kopija, ji naudojama tik tada, kai gyvos DB nėra.

## Tyčinis DB pakeitimas

Naudokite tik sąmoningai nusprendę grįžti prie repo seed duomenų:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1 -ReplaceDatabase
```

Paslauga pirmiausia sustabdoma. Esama DB nukopijuojama į:

`C:\ProgramData\PADS\MoneyFlow\Backups\monthly-money-flow-before-replace-YYYYMMDD-HHMMSS.db`

Tik tada gyva DB pakeičiama `seed\monthly-money-flow.db` kopija.

## Prieiga iš LAN

Serveryje patikrinkite:

```powershell
Invoke-WebRequest http://localhost:5000
Get-NetConnectionProfile
Get-NetFirewallRule -DisplayName "MoneyFlow LAN (TCP 5000)"
```

Kitame LAN kompiuteryje naršyklėje bandykite:

```text
http://SERVERIO-VARDAS:5000
http://SERVERIO-LAN-IP:5000
```

Serverio IPv4 adresą rasite:

```powershell
Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred |
  Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" }
```

Serverio tinklo profilis turi būti **DomainAuthenticated** arba **Private**. Scenarijus sąmoningai neatidaro prievado viešame (**Public**) tinkle.

## `ktpads.lt` paruošimas

Serverio kompiuteriui skirkite statinį IP arba DHCP rezervaciją. DNS pakeitimai nėra ir neturi būti koduojami programoje.

### A variantas — tiesioginis 5000 prievadas

Vidiniame DNS sukurkite `ktpads.lt` A įrašą į serverio LAN IP. Vartotojai atidarys:

`http://ktpads.lt:5000`

### B variantas — reverse proxy (rekomenduojama)

IIS, Caddy arba Nginx tame pačiame serveryje priima užklausas adresu:

`http://ktpads.lt`

ir perduoda jas į:

`http://localhost:5000`

Proxy turi perduoti įprastas `Host`, `X-Forwarded-For` ir `X-Forwarded-Proto` antraštes. Kai proxy patikrintas, galima apriboti 5000 prievado užkardos taisyklę iki reikiamo LAN potinklio arba panaikinti tiesioginę LAN prieigą, jei visi naudosis tik proxy.

Ar naudoti HTTPS, kas išduoda sertifikatą ir kaip juo pasitiki įmonės kompiuteriai, sprendžia tinklo administratorius. Viešas DNS nebūtinas, jei `ktpads.lt` naudojamas tik vidiniame DNS.

## Seed DB atnaujinimas iš dabartinių duomenų

Repo šaknyje administratoriaus PowerShell lange vykdykite:

```powershell
.\Update-SeedDatabase.ps1
```

Numatytasis šaltinis pirmiausia yra gyva `ProgramData` DB, o jei jos nėra — vietinė `data\monthly-money-flow.db`. Galima nurodyti konkretų kelią:

```powershell
.\Update-SeedDatabase.ps1 -SourceDatabase "D:\Kopijos\monthly-money-flow.db"
```

Jei naudojama gyva DB, scenarijus sustabdo paslaugą ir pabaigoje ją vėl paleidžia. Jis atsisako kopijuoti, jei liko WAL/SHM/journal failų. Jei sistemoje yra `sqlite3.exe`, vykdomas `PRAGMA integrity_check`; kitu atveju aiškiai parodomas įspėjimas. Į repo patenka tik `seed\monthly-money-flow.db`.

## Trikčių šalinimas

### Paslauga nepasileidžia

```powershell
Get-Service MoneyFlow
Get-WinEvent -LogName Application -MaxEvents 50 |
  Where-Object { $_.ProviderName -in '.NET Runtime','Application Error' } |
  Format-List TimeCreated,ProviderName,Message
```

Patikrinkite, ar yra .NET 10, ar DB aplankas pasiekiamas ir ar sukonfigūruotas API raktas. Po pataisymo:

```powershell
Restart-Service MoneyFlow
```

### 5000 prievadas jau naudojamas

```powershell
Get-NetTCPConnection -LocalPort 5000 -State Listen
Get-Process -Id (Get-NetTCPConnection -LocalPort 5000 -State Listen).OwningProcess
```

Sustabdykite konfliktuojančią programą arba pakeiskite jos prievadą. MoneyFlow turi likti 5000 prievade.

### Užkarda blokuoja LAN

```powershell
Get-NetFirewallRule -DisplayName "MoneyFlow LAN (TCP 5000)" |
  Format-List Enabled,Profile,Direction,Action
Test-NetConnection SERVERIO-LAN-IP -Port 5000
```

Patikrinkite, kad serverio tinklas nėra **Public** ir klientas yra tame pačiame leidžiamame LAN/VLAN.

### Trūksta .NET

```powershell
dotnet --list-sdks
```

Turi būti rodoma `10.x`. Įdiekite .NET 10 SDK iš oficialaus Microsoft puslapio ir kartokite diegimą.

### DB jau yra

Tai normalu: diegimas jos neperrašys. Eilinis atnaujinimas turi būti leidžiamas be `-ReplaceDatabase`.

### Seed DB nerasta

Patikrinkite, ar išskleidėte visą ZIP ir yra `seed\monthly-money-flow.db`. Nevykdykite diegimo iš dalinai nukopijuoto aplanko.

### Veikia serveryje, bet neveikia kitame kompiuteryje

Patikrinkite serverio LAN IP, `Test-NetConnection`, Domain/Private profilį, užkardą ir ar maršrutizatorius/VLAN leidžia ryšį. Pirmiausia bandykite IP adresu; jei IP veikia, o vardas ne — problema yra DNS.

### `ktpads.lt` nerandamas

```powershell
Resolve-DnsName ktpads.lt
nslookup ktpads.lt
```

Rezultatas turi būti serverio IP. Jei ne, pataisykite vidinio DNS A įrašą ir, jei reikia, kliente vykdykite `ipconfig /flushdns`.

## Ko netrinti ir ko neviešinti

- Netrinkite `C:\ProgramData\PADS\MoneyFlow` — ten yra gyvi duomenys ir atsarginės kopijos.
- Neįkelkite API raktų, slaptažodžių, tokenų, žurnalų ar importo tarpinių failų.
- Neįkelkite `*.db-wal`, `*.db-shm`, `*.db-journal`, `bin`, `obj`, `publish`, `data` ar testų išvesties.
- Seed DB turi realius įmonės duomenis, todėl GitHub repo privalo likti **PRIVATE**.
