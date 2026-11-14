# Administrator configuration

Only administrators can open configuration, account/customer management, migration, or update controls. Viewers see the monitoring dashboard and assigned customer information. A saved admin-page link redirects a viewer to Overview; all admin APIs independently require an administrator role. Revoking administrator access invalidates the user's current session.

## Console updates

The console checks the public `CheeseHeadChris96/vALERT` GitHub Releases repository every hour by default. Settings & audit provides automatic-check and preview-release preferences. Automatic detection does **not** automatically install a console update.

In **Update center**, administrators can:

1. Use **Check GitHub now** to refresh release information.
2. Use **Download installer** to download the verified Windows installer for offline/manual installation.
3. Use **Install console update**, then enter a fresh MFA code, to queue installation on the hosting Windows server.

The Windows task checks the queue every minute. It independently retrieves the selected release from the fixed GitHub repository; a request cannot provide its own download URL, executable, or install directory. Package sizes, manifest SHA-256 checksums, and GitHub asset digests (where supplied) are checked before installation.

**One-time upgrade requirement:** installations from preview.4 or earlier need the current `vALERT-Console-Setup.exe` run once as administrator. This installs the **vALERT Console Updater** task and its protected installation metadata. Development/non-Windows instances support detection and installer download but cannot perform in-portal Windows installation.

The service stops briefly. A restricted snapshot preserves the database, settings, SMTP secret, account/MFA keys, and agent identities. The new console starts with writes blocked until its version and health endpoint pass validation. If startup fails or installation is interrupted before commit, the updater restores the previous service command and data snapshot. A committed update is never rolled back over newly accepted reports. Agents retain unacknowledged reports and resend them when the console returns.

Versioned application files and protected snapshots are under `%ProgramFiles%\vALERT Console`. Update status is shown in the portal and recorded in `%ProgramData%\vALERT\Portal\console-update-status.json`. Review disk space and retain snapshots according to your backup policy. A manual installer is required when a future release changes the updater itself. Do not run a manual installer while update recovery is pending.

## SMTP

SMTP exists but is **disabled and unconfigured by default**. No hostname, account, password, sender address, or recipient is preconfigured. No mail was sent during implementation; automated tests use an in-memory transport.

Settings & audit includes:

- Enable/disable SMTP delivery.
- Host and port.
- Required STARTTLS (normally port 587) or TLS on connect (normally port 465).
- Optional username/password for authenticated delivery; leave the username blank for an authorized TLS relay.
- Sender email and display name.
- An explicit recipient and **Send test email** button.

Saving settings never sends a message. SMTP must be enabled and configured before a test can be sent. Passwords are encrypted using the portal's data-protection keys and never returned to the browser or written to the audit trail. An empty password preserves an existing one; selecting removal clears it. Changing the host, port, or account requires entering the credential again. TLS certificate validation cannot be disabled.

Backup notifications, account invitations, and password resets do not send email automatically. Their existing dashboard/manual-link workflows remain in place. OAuth-only mail providers require a separate integration; this SMTP implementation supports password authentication or an authorized relay.

## Other settings

The admin page also exposes the public HTTPS hostname and update preferences. Per-customer and per-job schedule grace periods remain editable from their management screens. HTTPS certificate bindings and listening ports remain local Windows configuration through `Configure-Portal.ps1`, so a remote settings change cannot accidentally remove the server's administrative connection.
