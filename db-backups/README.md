# Encrypted database backup

Šiame kataloge `Backup-MoneyFlowDb.ps1` sukuria tik šifruotą
`monthly-money-flow-latest.mfbackup` failą. Nešifruotų `.db`, `-wal`, `-shm` ar
`-journal` failų čia laikyti negalima.

Šifravimo frazė saugoma tik serverio
`C:\ProgramData\PADS\MoneyFlow\Configuration\backup-passphrase.txt` faile ir
turi būti saugiai perduota atkūrimo serverio administratoriui atskiru kanalu.
