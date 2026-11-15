# Windows console installation

## Requirements

- Windows Server 2019 or newer, x64, patched. A dedicated VM is recommended.
- An administrator to install the service, configure TLS, and set the firewall rule.
- A stable public hostname and a valid certificate for it, installed with its private key in `Local Computer > Personal`.
- Inbound HTTPS from customer collectors and your team; outbound HTTPS to GitHub for releases.
- Local durable storage and a backup plan for `%ProgramData%\vALERT\Portal`.

The installer includes the .NET runtime. It installs `vALERT.Portal` as Local Service, with an ACL-restricted data directory. Application executables are under `%ProgramFiles%\vALERT Console`.

## First installation

1. Download `vALERT-Console-Setup.exe` from the GitHub Releases page and run it as administrator.
2. On the server, open `http://localhost:5080`. This loopback-only listener is for local setup and recovery; it is not exposed through the firewall.
3. Read `%ProgramData%\vALERT\Portal\initial-setup.txt` locally as administrator. Enter that one-time token, your name/email, and a password of at least 12 characters. Do not post the token to GitHub.
4. Scan the displayed QR code with your authenticator app, then enter its six-digit code to confirm enrollment. Each account gets a cryptographically random secret; the QR code is generated locally by the console. If scanning is unavailable, expand the manual setup-key option. Save the one-use recovery codes privately.
5. Configure HTTPS in an elevated PowerShell window:

```powershell
& 'C:\Program Files\vALERT Console\tools\Configure-Portal.ps1' `
  -PublicUrl 'https://valert.rebootranch.net' `
  -CertificateThumbprint 'YOUR_CERTIFICATE_THUMBPRINT'
```

The helper grants Local Service read access to an RSA certificate's private key, configures the HTTPS listener, creates a Windows firewall rule, and restarts the service. For ECDSA or hardware-backed certificates, grant access through your certificate tooling and set `certificateThumbprint` in the settings file manually.

6. Point the public DNS A record for `valert.rebootranch.net` to your current public IPv4 address. Forward public TCP 443 to this server's static internal IP on port 443. Install a certificate trusted by the customer Windows servers with `valert.rebootranch.net` in its subject alternative names. Publish an AAAA record only if inbound IPv6 is configured too.
7. Verify the public URL from outside your network. Do not expose port 5080 or disable certificate validation.
8. Under Settings, verify the public HTTPS address. Under Update center, refresh releases and wait for the generic agent installer to be cached.
9. Create a customer and download its enrollment package. Follow `AGENT-SETUP.md`.

New installations default to `https://valert.rebootranch.net`. Upgrades preserve existing saved settings; run the command above to configure an existing installation. Setting a hostname does not create DNS records or install a certificate. Agent enrollment, reports, acknowledgements, and update downloads require TLS 1.2 or TLS 1.3 with certificate validation. Even the loopback HTTP listener rejects agent API traffic. The Windows PowerShell enrollment bootstrap uses TLS 1.2.

From another network, run `curl.exe --fail --verbose https://valert.rebootranch.net/health` without `--insecure`. Confirm a valid certificate for this hostname and a `ready` response. This validates public DNS, forwarding, and HTTPS without transmitting credentials. A certificate or connection failure must be fixed before enrolling agents; no HTTP fallback exists.

## Accounts

Administrators create accounts and share activation links themselves. No email is sent. An activation link expires after 24 hours. Users choose their password and enroll MFA. Administrative recovery links expire after one hour. Changing access revokes existing sessions; users sign in again.

If all administrator MFA methods are lost, a Windows administrator can stop the service and run the portal executable with `--recover-admin email@example.com`. The recovery link is written to `admin-recovery.txt` in the protected data directory, not to the terminal. Restart the service and use that link privately.

## Data and backups

Runtime files are under `%ProgramData%\vALERT\Portal`:

- `valert.db` and its SQLite journal files: customer history and account data.
- `keys`: ASP.NET data-protection keys required to decrypt MFA secrets and other protected account state.
- `update-signing.pem`: portal update-signing key. Agents pin the corresponding public key.
- `portal-settings.json`: hostname, TLS thumbprint, and migration state.
- `release-cache`: downloaded generic release artifacts.

Back up the database consistently and preserve the keys. A database-only restore is insufficient. The migration export produces a consistent encrypted snapshot, but intentionally pauses the source portal; it is not a scheduled-backup system.

## Existing installation

Run a newer console installer over the existing installation. The data directory is retained. The first preview uses schema version 1; future incompatible migrations must include a tested recovery path. Take a consistent backup before upgrading the console.

Uninstalling removes the service and application files; it retains monitoring data. Delete retained data separately only when that is intended.

## Dynamic public IP

Keep the same hostname in all enrollments. Update its DNS record when the public IP changes. Agents retain queued data until the hostname becomes reachable again. DNS caching and the delay before a manual update both affect the outage duration. Certificate renewal must be maintained independently.

## Troubleshooting

- Use Windows Services to inspect `vALERT.Portal` and Event Viewer for application errors.
- `http://localhost:5080/health` should return `ready`; it contains no customer data.
- A certificate access error usually means Local Service cannot read its private key.
- Initial setup requires the protected token file; there is no default password or public registration.
- The first preview is not Windows Authenticode-signed. Verify the GitHub release and SHA-256 manifest; follow your organization's software approval process.
