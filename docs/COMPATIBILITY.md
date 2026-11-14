# Veeam compatibility and validation

The collector negotiates VBR API revisions from 1.3-rev2 back to 1.1-rev0. Supported HTTP operations are read-only after authentication. Their availability differs by the actual Veeam build and role.

| VBR generation | Expected REST revision | Important limitation |
| --- | --- | --- |
| 12.0–12.2 | 1.1-rev0 through 1.1-rev2 | Limited job types; per-VM task-session endpoint absent |
| 12.3–12.3.1 | 1.2-rev0 / 1.2-rev1 | Per-VM task sessions available; job-type coverage remains limited |
| 13.0 | 1.3-rev0 / 1.3-rev1 | More job types; not identical to 13.1 coverage |
| 13.1 | 1.3-rev2 | Broadest currently implemented REST coverage |

The customer page reports API/permission gaps. A successful API call does not mean every job type on the server is exposed by that API version. Compare the first collected inventory with the Veeam console before relying on totals.

## Collection paths

- `GET /api/v1/jobs`, `/jobs/states`: definitions, schedules, current state.
- `GET /api/v1/sessions`: paginated history, initially up to 90 days retained at source.
- `GET /api/v1/sessions/{id}/taskSessions`: per-machine details where supported.
- `GET /api/v1/backupInfrastructure/repositories/states`: repository capacity.
- `GET /api/v1/serverInfo`: build information if permitted; otherwise local installed-file version.
- Veeam ONE: `/api/token` authentication, configurable API version, triggered alarms, repositories, scale-out repositories, backup servers, and proxies.

Veeam ONE metrics are shown as returned infrastructure details; history that ONE does not expose to the selected role cannot be fabricated. Version discovery for a remote ONE server and detailed chart/report endpoint expansion remain compatibility work.

## Schedules

Daily/selected-weekday and monthly schedules can establish a prior expected occurrence on enrollment. Other recurrence types use the next run observed from Veeam. If no usable next run is available (including some chained/continuous schedules), the UI shows that the schedule is unavailable rather than inventing a deadline. That job requires validation of its scheduling model before overdue monitoring can be relied on.

The default grace period is 30 minutes, editable per customer and per job. A running job is shown as running; this preview does not implement an independent maximum-runtime alarm. Manual and disabled jobs do not generate overdue status.

## Optional PowerShell compatibility adapter

`Collect-Veeam.ps1` uses documented Veeam job/session cmdlets to discover additional categories. The actual object properties and accepted session types differ by build. Unsupported properties produce unknown values/issues. Repository capacity in this mode may be unavailable; REST mode is preferred where it covers the required jobs.

Veeam documents Backup Administrator privileges for PowerShell, and current v13 builds have their own required PowerShell runtime. This adapter requires explicit configuration and live testing; it is not represented as a least-privilege, universally compatible substitute.

## Verification still required on customer infrastructure

Test one representative VBR12 server and one VBR13 server, including: job inventory equality, correct result transitions, VM details, repository units, scheduled/disabled/manual jobs, Chicago day boundaries, source TLS validation, initial backfill, a portal outage, and an interrupted agent update. No Veeam servers were connected during initial development.

## Primary references

- [VBR REST version map](https://helpcenter.veeam.com/references/vbr/13/rest/1.3-rev2/tag/SectionOverview/index.html)
- [Jobs](https://helpcenter.veeam.com/references/vbr/13/rest/1.3-rev2/tag/Jobs/index.html)
- [Sessions](https://helpcenter.veeam.com/references/vbr/13/rest/1.3-rev2/tag/Sessions/index.html)
- [Repositories](https://helpcenter.veeam.com/references/vbr/13/rest/1.3-rev2/tag/Repositories/index.html)
- [PowerShell permissions and runtime](https://helpcenter.veeam.com/docs/vbr/powershell/running_ps_sessions_windows.html?ver=13)
- [VBR service-account MFA](https://helpcenter.veeam.com/docs/vbr/userguide/mfa.html)
- [Veeam ONE API and roles](https://helpcenter.veeam.com/references/one/13/rest/index.html)
- [Veeam source history retention](https://helpcenter.veeam.com/docs/vbr/userguide/history_settings.html)
