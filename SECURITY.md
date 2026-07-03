# Security

## Supported use

MoneyFlow is an internal LAN application without end-user authentication. It is not designed or approved for direct internet exposure. The current maintained branch is `main`.

## Reporting

Report suspected vulnerabilities, exposed data, or leaked credentials to the internal project maintainer through the company-approved private channel. Do not open a public GitHub issue with real URLs, JSON exports, database files, screenshots containing company data, API keys, or personal data.

If a credential may have leaked, rotate it first, then investigate. Removing it from the latest commit is not enough because Git history and clones may retain it.

## Repository rules

- Never commit API keys, tokens, passwords, SharePoint links with access tokens, `.env` files, runtime logs, JSON exports, or SQLite databases.
- Use synthetic or irreversibly anonymized test fixtures only.
- Keep this repository private and review GitHub Pages, Actions artifacts, forks, and collaborator access.
- Run the CI build, package vulnerability check, JavaScript parse check, and all PowerShell integration tests before release.

## Deployment controls

- Restrict access with Domain/Private firewall profiles and, where possible, explicit LAN/VLAN source ranges.
- Prefer an internal reverse proxy with host validation and HTTPS.
- Give the service account least-privilege access to the application DB and API-key file.
- Keep tested backups outside the repository.
- Treat unauthenticated manual edit endpoints as trusted-LAN operations and monitor unexpected changes operationally.
