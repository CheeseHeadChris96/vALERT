# Security model

- Human access uses administrator-created accounts, password hashing with PBKDF2-SHA256 (600,000 iterations), mandatory RFC6238 TOTP, one-use recovery codes, account/IP throttling, cookie sessions, CSRF protection, and server-enforced customer scoping.
- Session security stamps invalidate access after account disabling or scope/role changes. TOTP replay is rejected. Fresh MFA is required for permanent customer deletion, account recovery, and migration export.
- The HTTPS listener uses the configured Windows certificate. The HTTP listener is bound to loopback only. No trusted-forwarded-header bypass is enabled.
- Agents initiate outbound HTTPS. Veeam passwords remain DPAPI-protected on the customer server; the portal stores hashes of agent credentials and enrollment tokens.
- Each enrollment token is customer-bound, expires after 24 hours, and enrolls one server. Customer archiving revokes its agents.
- The agent queue is flushed to disk before acknowledging local collection progress. The portal commits a report transaction before acknowledging it. Replays are idempotent and do not replace newer snapshots.
- Agent updates are signed by a portal-local RSA key. The immutable trust file is written by the administrator at enrollment. Local Service cannot change installed executable versions or updater trust; the SYSTEM updater verifies signatures before installing a package.
- Migration keys are intentionally portable across Windows machines. Data-protection XML and the update-signing private key rely on the restricted ProgramData ACL and your disk encryption/backups; they are not machine-DPAPI-encrypted on the portal. They are included only in the authenticated encrypted migration archive.

The first preview is not a security certification. No live customer Veeam systems were used during development. Validate the compatibility and update paths in a test environment before production deployment. Keep Windows, Veeam, the runtime, and vALERT patched.

The public repository contains product source and generic installation assets only. Do not submit customer database files, credentials, MFA secrets, private certificates, signing keys, or enrollment packages in issues or pull requests. Report security vulnerabilities privately to the repository owner through an agreed channel.
