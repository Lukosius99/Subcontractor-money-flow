# Architektūra

## Komponentai

1. SharePoint Excel failai yra verslo duomenų šaltinis.
2. Power Automate Cloud Flow ir Office Script suformuoja versijuotą JSON.
3. Power Automate Desktop (PAD) paima naujausią failą ir su `X-Api-Key` siunčia jį į vietinį API.
4. ASP.NET Core validuoja ir įrašo duomenis per EF Core į SQLite.
5. Tas pats procesas pateikia statinius `wwwroot` failus ir skaitymo API skyriaus naudotojams.

Programoje nėra atskiro frontend serverio, eilės ar išorinės DB. Tai sąmoningai paprasta vieno Windows LAN serverio architektūra.

## Repo žemėlapis

| Kelias | Turinys |
|---|---|
| `Endpoints/` | Minimal API maršrutai |
| `Dtos/` | Importo ir redagavimo užklausų / atsakymų tipai |
| `Models/` | EF Core DB modeliai |
| `Persistence/` | `DbContext` ir idempotentinis schemos paruošimas |
| `Services/` | Importas, validacija, normalizavimas, užklausos |
| `wwwroot/` | Statinė vartotojo sąsaja, fontai ir ExcelJS |
| `tests/` | PowerShell end-to-end / contract testai |
| `docs/` | Architektūra, importas, diegimas ir schemos peržiūra |
| `.github/workflows/` | CI patikros |

## Duomenų ir pasitikėjimo ribos

- Naršyklės skaitymo API ir rankinio redagavimo endpointai neturi naudotojo autentifikacijos; pasitikima LAN / reverse proxy riba.
- `POST /api/imports/*` papildomai reikalauja API rakto.
- Užklausos kūnas ribojamas iki 10 MB.
- Gamybos diagnostikos endpointas nepublikuojamas.
- SQLite ir API rakto failai laikomi `ProgramData`, ne programos ar repo kataloge.

## Schema ir atnaujinimai

Programa startuodama iškviečia `EnsureCreatedAsync` ir idempotentinius `SchemaInitializer` pakeitimus. Atskiro migracijų katalogo šiuo metu nėra. Dėl to schemos pakeitimas turi būti suderinamas su jau esančia DB ir patikrintas bent viena senesnės kopijos restauravimo repeticija.

Interaktyvi vizualizacija yra [subrangos-duomenu-kelias-3d](subrangos-duomenu-kelias-3d/index.html). Ji naudoja išorinį `unpkg.com` skriptą ir yra dokumentacijos priedas, ne gamybinės programos dalis.
