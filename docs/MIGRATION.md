# Migrating the portal

Use **Administration → Server migration**. This is a guided export/restore and DNS cutover; the application does not provision the destination Windows server or change your DNS provider.

1. Install the same vALERT release on the destination and stop its portal service.
2. On the source, enter a new migration passphrase (at least 16 characters) and a fresh MFA code, then choose **Freeze portal & download archive**.
3. The source rejects new reports with HTTP 503, and agents retain reports locally. Customer/account mutations are frozen. The encrypted archive includes a consistent database snapshot, data-protection keys, update-signing key, and settings.
4. Copy the `.vab` archive privately to the destination. Run the installed `Restore-Portal.ps1` in an elevated PowerShell window. It preserves any existing destination directory under a timestamped name, reads the passphrase securely, and restores into a new directory.
5. Configure the destination's HTTPS certificate using `Configure-Portal.ps1`. The certificate's private key is **not** exported in the migration archive; import or issue it through your normal certificate process.
6. Keep the same public hostname, update DNS/forwarding, and start the destination. Keep the source paused or stopped.
7. Verify customer, account, and agent counts; log in with MFA; confirm each agent reconnects and its queue drains. User sessions are invalidated on restore, while MFA enrollment and agent identities are retained.

Use **Cancel migration / resume** on the source only if the destination has not taken over. Do not run both portals as writable copies behind changing DNS. The export is a point-in-time transfer; switching back after the destination has accepted new reports requires another consistent transfer, not just flipping DNS back.

The archive uses AES-256-GCM, a random salt/nonce, and a PBKDF2-SHA256 derived key. A wrong passphrase or modified archive fails authentication. The archive contains sensitive material even though encrypted; protect it and its passphrase separately. The first preview's in-memory export/restore limit is 2 GB uncompressed and is intended for the initial single-server scale.
