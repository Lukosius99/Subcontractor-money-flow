# PADS MoneyFlow — Subcontractor Money Flow

Internal ASP.NET Core (.NET 10) app that tracks monthly subcontractor money flow.
It serves a static frontend from `wwwroot` and stores data in a SQLite database.
In production it runs as a Windows service named **MoneyFlow** on **port 5000**.

## Subrangos pinigų srauto 3D duomenų kelio schema

➡️ **[Atidaryti interaktyvią 3D duomenų kelio schemą](https://lukosius99.github.io/Subcontractor-money-flow/subrangos-duomenu-kelias-3d/)**

[![Subrangos pinigų srauto 3D schemos peržiūra](docs/subrangos-duomenu-kelias-3d/preview.png)](https://lukosius99.github.io/Subcontractor-money-flow/subrangos-duomenu-kelias-3d/)

*Spustelėkite peržiūrą, kad atidarytumėte valdomą interaktyvią schemą GitHub Pages svetainėje.*

Ši 3D dokumentacijos schema parodo, kaip mėnesiniai aktavimo duomenys ir sutartinės vertės keliauja nuo PADS SharePoint, DVS / DocLogix, Dynamics, SAP BO, DB SharePoint ir PAD iki lokalios API, SQLite duomenų bazės ir web sąsajos. Schema skirta dokumentacijai, todėl nėra rodoma pačioje gamybinėje programos sąsajoje. [Peržiūrėti jos šaltinio failus](docs/subrangos-duomenu-kelias-3d/).

GitHub Pages svetainė automatiškai publikuojama iš `docs` aplanko. Repozitoriją taip pat galima parsisiųsti ir failą `docs/subrangos-duomenu-kelias-3d/index.html` atidaryti naršyklėje; 3D bibliotekai įkelti reikalingas interneto ryšys. Išorinių automatizacijų dalys pažymėtos „Pagal proceso aprašą“, kodo patvirtintos dalys pažymėtos „Patvirtinta kode“, o neaiškumai, įskaitant sutartinių duomenų skripto neatitikimą, pažymėti „Reikia patikrinti“.

Vietinei peržiūrai komandą paleiskite iš repozitorijos pagrindinio aplanko:

```powershell
python -m http.server 8765 --directory ".\docs\subrangos-duomenu-kelias-3d"
```

Tada atidarykite [http://localhost:8765/](http://localhost:8765/).

Jautrios reikšmės, API raktai, tokenai ir slaptažodžiai schemoje sąmoningai nerodomi.

### Pastabos / tikrintini dalykai

- Pateiktas contracted Office Script atrodo kaip mėnesinio srauto scriptas. Reikia patikrinti tikrą sutartinių verčių eksporto scriptą ir `/api/imports/contracts` laukiamą JSON formatą. Faktinė API laukia `rows` ir (arba) `projectValueRows`, ne mėnesinio `subranga` / `SMD` formato.
- Pagal proceso aprašą PAD sėkme laiko tik statusą `200`; verta patikrinti, ar neturėtų būti priimami ir kiti `2xx` atsakymai.
- PAD užklausa turi siųsti JSON kūną su `Content-Type: application/json`. API raktai dokumentacijoje nerodomi.
- Eilutės be objekto kodo praleidžiamos, todėl importo perspėjimus reikia stebėti.

---

## Deploy on a new computer (the easy way)

1. **Install the .NET 10 SDK** — https://dotnet.microsoft.com/download/dotnet/10.0
2. **Get the code** — either:
   - `git clone https://github.com/Lukosius99/Subcontractor-money-flow.git`, or
   - download the repo as a ZIP from GitHub and extract it anywhere you like.
3. **Run the deployer as Administrator:**
   - Right-click `Deploy-MoneyFlow.ps1` → **Run with PowerShell** (it will prompt for admin), or from an elevated PowerShell:
     ```powershell
     powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
     ```
4. Open **http://localhost:5000**.

That single script publishes the app in place (into a `publish\` subfolder), installs and starts the Windows service, and sets up the database.

---

## Your data is safe

- The live database lives at `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db` — **outside** the app folder, so updating or re-deploying the code never touches it.
- A snapshot of the current data ships in `seed\monthly-money-flow.db`.
- On deploy, the script restores that snapshot **only if no database exists yet** on the machine. If a database is already there, it is left completely untouched. So you can safely re-run the deployer to update the app without losing data.

### Updating an already-deployed machine
Pull the latest code (`git pull` or re-download), then re-run `Deploy-MoneyFlow.ps1`. It stops the service, republishes, keeps your existing database, and restarts.

### Starting fresh / replacing data on the new machine
If you want the new machine to start from the bundled snapshot, delete `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db` (and any `-wal`/`-shm` next to it) **before** running the deployer, and it will restore the snapshot.

---

## Useful commands

```powershell
Start-Service MoneyFlow      # start  (Administrator)
Stop-Service  MoneyFlow      # stop   (Administrator)
Get-Service   MoneyFlow      # status
```

## What is / isn't in this repo

- **Included:** source code, the deploy script, and the `seed\` database snapshot.
- **Excluded** (see `.gitignore`): build output (`bin/`, `obj/`, `publish/`), the working `data/` database, and the large throwaway `test-data/` fixtures.
