# vALERT

An internal, self-hosted backup operations portal for multiple Veeam customers. The Windows collector sends monitoring data outbound over HTTPS; the portal displays customer and job health, daily history, capacity, and agent status.

**Status: first preview.** This release has automated behavior/access tests and a Windows installer smoke test. It has not been validated against a live customer Veeam environment. Start with a test Veeam server and review [version compatibility](docs/COMPATIBILITY.md) before rollout.

## Download and install

Download **`vALERT-Console-Setup.exe`** from [Releases](https://github.com/CheeseHeadChris96/vALERT/releases) and install it on your central Windows Server. It includes the .NET runtime and installs a Windows service; no separate runtime installation is required.

1. Follow the [Windows console installation guide](docs/WINDOWS-INSTALL.md).
2. Configure your public HTTPS hostname and certificate.
3. Create the initial administrator using the local setup token, then enroll authenticator-app MFA.
4. Create a customer and download its customer-specific enrollment ZIP.
5. Extract the ZIP on that customer's Windows Veeam server and run `Install-vALERT.ps1` as administrator. Follow [agent setup](docs/AGENT-SETUP.md).

The first collection attempts to import up to 90 days of history still retained by Veeam. Subsequent collections run every 15 minutes. The portal distinguishes a connected agent from a successful Veeam collection.

## Included behavior

- Separate customer and job status charts, with per-customer access enforced on the server.
- Customer drill-down with daily job heat maps, run/VM details, repositories, server inventory, and optional Veeam ONE information.
- Job-type tabs appear when discovered; errors do not silently remove previously known jobs.
- Latest completed result determines job/day color. A successful retry turns that day's cell green; prior attempts remain in history.
- Enabled scheduled jobs become red **Overdue** after the expected run plus a configurable grace period (initial default 30 minutes). Manual jobs are exempt; disabled jobs are excluded from current totals.
- Schedules follow their actual daily, weekly, or monthly recurrence. Observed expectations are retained so an advancing next-run timestamp cannot hide a missed run.
- Capacity: green above 10% free, amber above 5% through 10%, red at 5% or less. Unavailable capacity is unknown.
- Stale reporting at 45 minutes without a successful collection. Unknown/stale counts are separate from known results.
- 90-day history and Chicago date boundaries, including daylight saving time.
- Admin-created accounts, mandatory TOTP MFA, one-use recovery codes, account disabling, MFA recovery, and all/specific-customer access.
- Multiple Veeam servers per customer. Customer archiving revokes agent access; permanent deletion requires fresh MFA.
- Customer-bound, expiring, single-use enrollment packages. Locally protected Veeam credentials; no Veeam passwords are uploaded to the portal.
- Durable agent queues with explicit acknowledgements and idempotent ingestion.
- Automatic agent updates, update status, manual retry, protected updater trust, startup validation, interrupted-update recovery, and rollback without deleting monitoring queues.
- Console updates: automatic hourly GitHub detection, manual checking and installer downloads, and admin-initiated installation with a protected data snapshot and startup rollback.
- Admin-only SMTP settings with encrypted credentials, required TLS, and an explicit test-email action. SMTP starts disabled and unconfigured.
- Encrypted migration export and a Windows restore tool preserving customer, account/MFA, and agent identities.
- Backup alerts remain dashboard-only. Configuring SMTP does not enable notification rules or send email automatically.

## Hosting

Designed initially for 30 customers on a dedicated Windows VM, with SQLite in WAL mode and indexed history. Keep the data directory on local durable storage. Capacity depends on job frequency, VM counts, and retained detail; 30 customers is a design target, not a load-test certification. The migration workflow supports moving to a larger server. A multi-node database backend is a future scaling step, not included in this preview.

A stable DNS hostname is required. Dynamic public IPs are supported operationally by updating that DNS record. During a DNS or portal outage, collected reports remain on each agent's disk. No data-preservation claim covers loss of that disk; back up the portal and protect the agent data directory.

## Source layout

| Directory | Purpose |
| --- | --- |
| `src/Valert.Portal` | ASP.NET Core portal/API and browser UI |
| `src/Valert.Agent` | Windows service, REST collectors, optional PowerShell adapter, durable queue |
| `src/Valert.Updater` | Separate privileged update worker; validates a pinned portal signature |
| `src/Valert.Core` | Shared contracts, status/scheduling rules, cryptography |
| `installer` | Windows installer definitions and setup/migration scripts |
| `tests/Valert.Tests` | Status, security, ingestion, migration, and access-isolation tests |
| `.github/workflows` | Windows build/test/installer validation and GitHub Releases publication |

## Build

Install the .NET 10 SDK. Then:

```sh
dotnet restore vALERT.sln
dotnet test tests/Valert.Tests
dotnet build vALERT.sln
```

On Windows with Inno Setup 6 installed:

```powershell
./scripts/build-windows.ps1 -Version 0.1.0-preview.1
```

GitHub Actions produces self-contained Windows x64 applications, installers, SHA-256 manifests, test evidence, and a preview release for each successful source build on `main`. Release publication occurs only after tests and an installed-service smoke test pass.

## Security and operating limits

Read [security](SECURITY.md), [compatibility](docs/COMPATIBILITY.md), and [migration](docs/MIGRATION.md). This repository and its generic installers are public. Runtime databases, private keys, customer enrollment files, and credentials must remain private.

When upgrading from preview.4 or earlier, run the new Windows console installer once to install its privileged update task. Future console releases can then be installed through the admin Update center. See [console updates and SMTP](docs/ADMIN-CONFIGURATION.md).

Installers in this first preview are **not Authenticode-signed**. Automatic agent packages are separately verified using the portal's signing key pinned during enrollment. Obtaining and integrating an organizational Windows code-signing certificate remains a deployment task.
