# Security

## Supported use

MoneyFlow is an internal LAN application without end-user authentication. All internal users may use the read-only UI and GET/HEAD API endpoints. Manual Web UI edits require a separate edit passphrase. PAD imports, database restore, and other protected automation endpoints require the shared `X-Api-Key`. It is not designed or approved for direct internet exposure. The current maintained branch is `main`.

## Reporting

Report suspected vulnerabilities, exposed data, or leaked credentials to the internal project maintainer through the company-approved private channel. Do not open a public GitHub issue with real URLs, JSON exports, database files, screenshots containing company data, API keys, or personal data.

If a credential may have leaked, rotate it first, then investigate. Removing it from the latest commit is not enough because Git history and clones may retain it.

## Repository rules

- Never commit API keys, edit or backup passphrases, tokens, passwords, SharePoint links with access tokens, `.env` files, runtime logs, JSON exports, or unencrypted SQLite databases.
- Use synthetic or irreversibly anonymized test fixtures only.
- Keep this repository private and review GitHub Pages, Actions artifacts, forks, and collaborator access.
- Run the CI build, package vulnerability check, JavaScript parse check, and all PowerShell integration tests before release.

## Deployment controls

- Restrict access with Domain/Private firewall profiles and, where possible, explicit LAN/VLAN source ranges.
- When project-approved internal HTTP is used, isolate it to trusted LAN/VLAN ranges, do not create internet-facing NAT, and keep reverse-proxy request-header logging disabled.
- Give the service account least-privilege access to the application DB and credential files.
- Keep only encrypted `.mfbackup` artifacts in Git. Store the backup passphrase in the approved company password vault and transfer it separately from the repository.
- Keep the API key limited to PAD and approved operators. Do not reuse it as the edit or backup passphrase.
- Database backup uses the API-key-protected service endpoint so operators never receive direct read access to the live SQLite file.
- Store only the PBKDF2 hash of the edit passphrase on the server. The browser keeps the entered passphrase only in current page memory and never persists it to local storage.
- Treat the shared API key as restore authority: `POST /api/maintenance/db-restore` can replace all application data. The endpoint accepts only a validated `.mfbackup` or standalone `.db`, creates a rollback copy, and is unavailable without the key.
