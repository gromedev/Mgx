# Mgx Examples

Runnable scripts covering common Microsoft Graph scenarios. Each script is self-contained and includes the required scopes in its header comment.

Scripts are grouped by purpose - pick the directory that matches what you are trying to do.

## Prerequisites

```powershell
Install-Module Mgx                       # once
Import-Module Mgx
Connect-MgGraph -Scopes "User.Read.All", "Group.Read.All"   # adjust scopes per script
```

## Scripts

### Getting Started (`getting-started/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [get-all-users.ps1](getting-started/get-all-users.ps1) | Stream all users to the console | `User.Read.All` |
| [export-users-to-jsonl.ps1](getting-started/export-users-to-jsonl.ps1) | Export all users to a JSONL file with checkpoint/resume | `User.Read.All` |
| [beta-endpoint.ps1](getting-started/beta-endpoint.ps1) | Access beta endpoints without installing extra modules | `User.Read.All` |

### Fan-Out & Relations (`fan-out-and-relations/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [get-managers-fan-out.ps1](fan-out-and-relations/get-managers-fan-out.ps1) | Fetch every user's manager concurrently | `User.Read.All` |
| [enrich-users-with-manager.ps1](fan-out-and-relations/enrich-users-with-manager.ps1) | Attach manager as a property on each user object | `User.Read.All` |
| [chained-relation-expansion.ps1](fan-out-and-relations/chained-relation-expansion.ps1) | Enrich users with manager + licenses in one pass | `User.Read.All` |
| [group-members-multipage.ps1](fan-out-and-relations/group-members-multipage.ps1) | Stream all members from all groups concurrently | `Group.Read.All`, `User.Read.All` |

### Reporting & Audit (`reporting-and-audit/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [disabled-accounts-report.ps1](reporting-and-audit/disabled-accounts-report.ps1) | Report all disabled accounts | `User.Read.All` |
| [guest-users-report.ps1](reporting-and-audit/guest-users-report.ps1) | Report all guest (external) accounts | `User.Read.All` |
| [stale-devices-report.ps1](reporting-and-audit/stale-devices-report.ps1) | Report devices inactive for 90+ days | `Device.Read.All` |
| [app-secrets-expiry.ps1](reporting-and-audit/app-secrets-expiry.ps1) | Find app secrets and certificates expiring within 30 days | `Application.Read.All` |
| [conditional-access-export.ps1](reporting-and-audit/conditional-access-export.ps1) | Export all Conditional Access policies (beta endpoint) | `Policy.Read.All` |
| [export-sign-in-logs.ps1](reporting-and-audit/export-sign-in-logs.ps1) | Export sign-in logs to JSONL with checkpoint/resume | `AuditLog.Read.All` |

### Bulk Writes & Batching (`bulk-writes-and-batching/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [bulk-update.ps1](bulk-writes-and-batching/bulk-update.ps1) | PATCH multiple users via `$batch` (20 per HTTP call) | `User.ReadWrite.All` |
| [dead-letter-retry.ps1](bulk-writes-and-batching/dead-letter-retry.ps1) | Bulk create users with dead-letter tracking for failures | `User.ReadWrite.All` |
| [bulk-delete-whatif.ps1](bulk-writes-and-batching/bulk-delete-whatif.ps1) | Bulk delete stale guests with `-WhatIf` preview | `User.ReadWrite.All` |
| [mixed-endpoint-batch.ps1](bulk-writes-and-batching/mixed-endpoint-batch.ps1) | Query users, groups, apps, and SKUs in one HTTP call | `User.Read.All`, `Group.Read.All`, `Application.Read.All` |

### Delta & Sync (`delta-and-sync/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [delta-sync.ps1](delta-and-sync/delta-sync.ps1) | Full sync on first run, incremental changes thereafter | `User.Read.All` |
| [drive-delta-resumable.ps1](delta-and-sync/drive-delta-resumable.ps1) | Resumable OneDrive/SharePoint delta sync with checkpointing | `Files.Read.All` |
| [tenant-change-feed.ps1](delta-and-sync/tenant-change-feed.ps1) | A resumable tenant-wide change feed, baselined with `-Latest` | `Group.Read.All`, `Application.Read.All` |

### Drive Content (`drive-content/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [partial-content-hash.ps1](drive-content/partial-content-hash.ps1) | Identify files by hashing a byte range instead of downloading them whole | `Files.Read.All` |
| [drive-content-triage.ps1](drive-content/drive-content-triage.ps1) | Sniff 4 KB of each file, then download only what survives triage | `Files.Read.All` |

### Resilience & Telemetry (`resilience-and-telemetry/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [resilience-for-existing-scripts.ps1](resilience-and-telemetry/resilience-for-existing-scripts.ps1) | Add retry/circuit breaker to existing SDK scripts (zero code changes) | `User.Read.All` |
| [resilience-status.ps1](resilience-and-telemetry/resilience-status.ps1) | Check, enable, and disable SDK resilience injection | `User.Read.All` |
| [telemetry.ps1](resilience-and-telemetry/telemetry.ps1) | View request counts, retries, and throttle events for the session | `User.Read.All` |
| [tune-rate-limits.ps1](resilience-and-telemetry/tune-rate-limits.ps1) | Tune rate limiter, retry count, and timeouts at runtime | `User.Read.All` |
| [benchmark-resilience.ps1](resilience-and-telemetry/benchmark-resilience.ps1) | Benchmark: bare SDK vs MgxResilience vs Invoke-MgxRequest vs Export | `User.Read.All` |
| [resource-unit-budgeting.ps1](resilience-and-telemetry/resource-unit-budgeting.ps1) | Measure what query shapes cost in resource units, and size a fan-out from the measurement | `Group.Read.All`, `User.Read.All` |
| [pacing-observability.ps1](resilience-and-telemetry/pacing-observability.ps1) | Watch the adaptive pacer: slow start, per-workload buckets, and which throttle signals are real | `User.Read.All`, `Files.Read.All` |

## Cmdlet Coverage

Every Mgx cmdlet is demonstrated in at least one script:

| Cmdlet | Scripts |
|--------|---------|
| `Invoke-MgxRequest` | get-all-users, beta-endpoint, get-managers-fan-out, enrich-users-with-manager, chained-relation-expansion, group-members-multipage, disabled-accounts-report, guest-users-report, stale-devices-report, app-secrets-expiry, conditional-access-export, bulk-delete-whatif, partial-content-hash, drive-content-triage, telemetry, tune-rate-limits, benchmark-resilience, resource-unit-budgeting, pacing-observability |
| `Invoke-MgxBatchRequest` | bulk-update, dead-letter-retry, mixed-endpoint-batch |
| `Export-MgxCollection` | export-users-to-jsonl, export-sign-in-logs, benchmark-resilience |
| `Expand-MgxRelation` | enrich-users-with-manager, chained-relation-expansion |
| `Sync-MgxDelta` | delta-sync, drive-delta-resumable, tenant-change-feed |
| `Enable-MgxResilience` | resilience-for-existing-scripts, resilience-status, benchmark-resilience |
| `Disable-MgxResilience` | resilience-status, benchmark-resilience |
| `Get-MgxResilience` | resilience-status |
| `Set-MgxOption` | tune-rate-limits, pacing-observability |
| `Get-MgxOption` | tune-rate-limits |
| `Get-MgxTelemetry` | telemetry, benchmark-resilience, resource-unit-budgeting, pacing-observability, partial-content-hash, drive-content-triage |
| `Get-MgxContent` | partial-content-hash, drive-content-triage |
