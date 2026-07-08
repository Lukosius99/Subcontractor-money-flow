# Windows LAN diegimas

## Paskirtis ir ribos

Programa skirta vienam kontroliuojamam Windows LAN serveriui. Numatytasis gamybinis adresas yra `http://0.0.0.0:5000`; ugniasienės taisyklė atidaro TCP 5000 tik Domain ir Private profiliams. Interneto prieigos nesuteikite.

## Pirmas diegimas

1. Įdiekite .NET 10 SDK.
2. Atsisiųskite visą privataus repo ZIP arba klonuokite repo.
3. Administratoriaus PowerShell lange vykdykite:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Deploy-MoneyFlow.ps1
   ```

4. Scenarijus publikuoja programą į `C:\Program Files\PADS\MoneyFlow`, sukuria Windows paslaugą `MoneyFlow`, paruošia `C:\ProgramData\PADS\MoneyFlow` ir ugniasienės taisyklę.
5. Jei DB nėra, programa pirmo starto metu sukuria tuščią schemą.
6. Vietinis operatorius paleidžia `C:\Program Files\PADS\MoneyFlow\Set-MoneyFlowApiKey.ps1` ir įveda PAD naudojamą raktą.

Norint tik pirmo diegimo metu atkurti patikrintą išorinę kopiją:

```powershell
.\Deploy-MoneyFlow.ps1 -InitialDatabase "D:\SecureBackups\monthly-money-flow.db"
```

Šaltinis turi būti už repo ribų, uždarytas ir be aktyvių `-wal`, `-shm` ar `-journal` failų. Jei gyva DB jau yra, parametras jos neperrašo.

## Atnaujinimas

Dar kartą paleiskite tą pačią komandą be `-InitialDatabase`. Scenarijus sustabdo paslaugą, pakeičia programos failus, palieka gyvą DB ir vėl paleidžia paslaugą.

## Atsarginė kopija ir atkūrimas

Paprasčiausias kelias — paruošti scenarijai repo šaknyje (abu patys pasiprašo administratoriaus teisių):

- `Backup-MoneyFlowDb.bat` — sustabdo paslaugą, nukopijuoja DB į `C:\ProgramData\PADS\MoneyFlow\Backups` (laikoma 30 naujausių), vėl paleidžia paslaugą, patikrina `/health` ir naujausią kopiją įkelia į `db-backups/monthly-money-flow-latest.db` repozitorijoje.
- `Restore-MoneyFlowDb.bat` — parodo rastas kopijas, prieš atstatymą dabartinę DB išsaugo kaip `pre-restore-*`, atstato pasirinktą kopiją ir patikrina `/health`.

Rankinė procedūra (jei scenarijų naudoti negalima):

1. Sustabdykite `MoneyFlow` paslaugą.
2. Patikrinkite, kad nėra DB `-wal`, `-shm` ar `-journal` failų; jei yra, paslaugą trumpam paleiskite ir korektiškai sustabdykite arba naudokite SQLite backup įrankį.
3. Nukopijuokite `C:\ProgramData\PADS\MoneyFlow\monthly-money-flow.db` į prieigos kontrole apsaugotą vietą.
4. Atkuriant sustabdykite paslaugą, atskirai išsaugokite esamą DB, pakeiskite failą ir paleiskite paslaugą.
5. Patikrinkite `/health`, projektų sąrašą ir paskutinio importo būseną.

Reguliariai atlikite restauravimo repeticiją; vien kopijos buvimas neįrodo, kad ji tinkama.

## Tinklas ir reverse proxy

- Serveriui skirkite DHCP rezervaciją arba statinį LAN IP.
- Apribokite ugniasienės `RemoteAddress` iki reikalingo VLAN / potinklio, kai jį žino tinklo administratorius.
- Rekomenduojama IIS, Caddy ar Nginx priimti vidinį DNS vardą ir proxy perduoti į `http://localhost:5000`.
- Po proxy patikros uždarykite tiesioginę 5000 prieigą klientams, jei ji nebereikalinga.
- HTTPS ir vidinio sertifikato pasitikėjimą tvarko tinklo administratorius.
- `AllowedHosts: "*"` paliktas dėl skirtingų vidinių vardų; reverse proxy turi validuoti leistinus hostus.

## Failų teisės

Paslaugos paskyrai suteikite tik skaitymo / rašymo teises į jos `ProgramData` aplanką. Paprastiems vietiniams naudotojams diegimo scenarijus leidžia keisti tik `Configuration` aplanką API raktui. API rakto failo, DB ir backup katalogų nesidalinkite per SMB be aiškaus poreikio.

## Paslaugos komandos

```powershell
Get-Service MoneyFlow
Start-Service MoneyFlow
Stop-Service MoneyFlow
Restart-Service MoneyFlow
```

Papildoma diagnostika: [troubleshooting.md](troubleshooting.md).
