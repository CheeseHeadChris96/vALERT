# Agent enrollment and operation

Create the customer in the vALERT admin portal, then select **Download agent package**. Each package enrolls one Windows server. A customer can have several servers; generate a separate package for each.

## Install

1. Copy the customer-specific enrollment ZIP to the customer's Veeam server and extract it.
2. From an elevated PowerShell window, run `Install-vALERT.ps1`. If your execution policy blocks downloaded scripts, review and unblock this downloaded script according to your organization's policy.
3. The bootstrap requests the current installer over authenticated HTTPS, downloads the generic installer from GitHub, and verifies its SHA-256 against the portal's expected value.
4. Setup asks for the Veeam REST endpoint, a dedicated Veeam monitoring account, and its password. Password entry is hidden and passwords are not passed in command-line arguments.
5. Use a trusted Veeam TLS certificate. For a self-signed certificate, provide its verified SHA-256 fingerprint. The agent never has a `skip certificate validation` option.
6. Optionally configure the customer's Veeam ONE endpoint and dedicated monitoring identity. **The configured ONE endpoint must contain only this customer's infrastructure.** Do not connect a shared, multi-customer ONE server in this preview: its returned inventory and alarms are associated with the selected customer.
7. The service starts, enrolls to that customer, and begins collection. Initial 90-day import may take longer than later collections.

REST mode is preferred. VBR 12/13 API coverage varies; see `COMPATIBILITY.md`. Optional PowerShell collection requires the appropriate PowerShell version and a Windows service identity with Veeam Backup Administrator privileges. Selecting it does not grant those privileges automatically. Configure the Windows service identity separately and grant it access to the agent data directory. Do not use a domain administrator account.

## Credentials and permissions

- VBR credentials should belong to a dedicated service account with the narrowest supported role. Backup Viewer can read the documented monitoring endpoints in supported API versions; VBR build-info access may be denied, in which case the collector reads the locally installed file version.
- Configure VBR service-account authentication as required by that Veeam version. This is independent of the mandatory MFA for humans signing in to vALERT.
- ONE read-only permissions omit some endpoints. Such omissions appear as collection issues.
- Credentials are Windows DPAPI-protected at rest and the data directory is ACL-restricted. They stay on the customer server.

## Files and updates

`%ProgramData%\vALERT\Agent` stores protected configuration, collection cursors, queued reports, and update status. Never remove this directory to repair an update. The queue advances only after an exact portal acknowledgement. Retried reports are idempotent, and old queued snapshots do not overwrite newer portal state.

Executable versions and immutable updater trust are under `%ProgramFiles%\vALERT Agent`. The agent runs as Local Service by default. A separate SYSTEM scheduled task checks a signed update request every 15 minutes. It verifies the pinned portal key, version, HTTPS origin, package length, and SHA-256 before using the package. It retains the previous service command, validates startup, and rolls back on failure. An interrupted update is recovered on the next scheduled task run.

Use **Update / retry** in the portal to retry a failed update. An offline agent must reconnect before it can receive this request. Alternatively, install the newer generic agent installer over the existing installation; existing configuration and queues remain in ProgramData. The generic installer does not enroll a new server by itself.

The first preview's auto-update worker is delivered separately from the versioned collector. Updates to the privileged updater itself use the full agent installer. Retained executable versions should not be manually removed while an update/recovery is in progress.

Protect the underlying disk. Queue preservation during software updates cannot recover reports after disk failure. If disk space is exhausted, collection reports errors and does not silently delete old queued data.

## Enrollment recovery

The enrollment token expires after 24 hours and is consumed once. If setup is interrupted after remote enrollment but before the local configuration is saved, revoke the unused agent record and generate a fresh enrollment package. Existing monitoring history is never transferred to a different customer by editing an enrollment token.
