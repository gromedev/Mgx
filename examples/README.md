# Mgx Examples

Runnable scripts for things people actually do against Microsoft Graph. Each one is self-contained and names the scopes it needs in its header comment.

Scripts are grouped by the job, not by the cmdlet - pick the directory that matches what you are trying to get done. Within a directory, the shorter script usually comes first and the longer one builds on it.

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
| [export-users-to-csv.ps1](getting-started/export-users-to-csv.ps1) | Export all users to CSV, flattening the nested properties | `User.Read.All` |
| [delta-users-sync.ps1](getting-started/delta-users-sync.ps1) | Full sync on first run, changed users only thereafter | `User.Read.All` |

### Reporting & Audit (`reporting-and-audit/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [user-directory-report.ps1](reporting-and-audit/user-directory-report.ps1) | Every enabled user with manager and licenses attached | `User.Read.All` |
| [download-groups-and-members.ps1](reporting-and-audit/download-groups-and-members.ps1) | Download every group and all memberships, nesting resolved locally, with a progress bar | `Group.Read.All`, `User.Read.All` |
| [group-membership-report.ps1](reporting-and-audit/group-membership-report.ps1) | Direct vs effective membership of every group, nesting resolved server-side | `Group.Read.All`, `User.Read.All` |
| [inactive-users-report.ps1](reporting-and-audit/inactive-users-report.ps1) | Users not seen in 90 days, and those who never signed in | `User.Read.All`, `AuditLog.Read.All` |
| [license-report.ps1](reporting-and-audit/license-report.ps1) | Seats per SKU, who holds them, and which are on disabled accounts | `Organization.Read.All`, `User.Read.All` |
| [app-secrets-expiry.ps1](reporting-and-audit/app-secrets-expiry.ps1) | Find app secrets and certificates expiring within 30 days | `Application.Read.All` |
| [export-sign-in-logs.ps1](reporting-and-audit/export-sign-in-logs.ps1) | Export sign-in logs to JSONL with checkpoint/resume | `AuditLog.Read.All` |

### Bulk Writes & Batching (`bulk-writes-and-batching/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [tenant-snapshot.ps1](bulk-writes-and-batching/tenant-snapshot.ps1) | Users, groups, apps, and licenses in one HTTP round trip | `User.Read.All`, `Group.Read.All`, `Application.Read.All` |
| [bulk-create-users-from-csv.ps1](bulk-writes-and-batching/bulk-create-users-from-csv.ps1) | Create users from a CSV, keeping failures for replay | `User.ReadWrite.All` |
| [assign-licenses.ps1](bulk-writes-and-batching/assign-licenses.ps1) | License everyone who is missing one, seat-checked first | `User.ReadWrite.All`, `Organization.Read.All` |
| [offboard-users.ps1](bulk-writes-and-batching/offboard-users.ps1) | Block sign-in, revoke sessions, and return the licenses | `User.ReadWrite.All`, `Directory.ReadWrite.All` |
| [bulk-delete-whatif.ps1](bulk-writes-and-batching/bulk-delete-whatif.ps1) | Bulk delete stale guests with `-WhatIf` preview | `User.ReadWrite.All` |

### Change Feeds (`change-feeds/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [tenant-inventory.ps1](change-feeds/tenant-inventory.ps1) | Snapshot every root collection `$metadata` declares, resumable | whatever read scopes you have |
| [group-change-feed.ps1](change-feeds/group-change-feed.ps1) | Baseline one resource with `-Latest`, then log only what changes | `Group.Read.All` |
| [tenant-change-feed.ps1](change-feeds/tenant-change-feed.ps1) | The same across several resources, resumable, as a scheduled job | `Group.Read.All`, `Application.Read.All` |

### Drive Content (`drive-content/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [drive-delta-resumable.ps1](drive-content/drive-delta-resumable.ps1) | Resumable OneDrive/SharePoint drive enumeration to JSONL | `Files.Read.All` |
| [download-user-onedrive.ps1](drive-content/download-user-onedrive.ps1) | Download a user's whole OneDrive, preserving the folder tree | `Files.Read.All` |
| [drive-content-triage.ps1](drive-content/drive-content-triage.ps1) | Sniff 4 KB of each file, then download only what survives triage | `Files.Read.All` |

### Resilience & Telemetry (`resilience-and-telemetry/`)

| Script | Description | Scopes |
|--------|-------------|--------|
| [resilience-status.ps1](resilience-and-telemetry/resilience-status.ps1) | Add retry and circuit breaking to existing SDK scripts, then check and remove it | `User.Read.All` |
| [progress-for-long-runs.ps1](resilience-and-telemetry/progress-for-long-runs.ps1) | A live status line for a streamed fan-out: percent, rate, ETA, resource units | `Group.Read.All`, `User.Read.All` |
| [resource-unit-budgeting.ps1](resilience-and-telemetry/resource-unit-budgeting.ps1) | Measure what query shapes cost in resource units, and size a fan-out from the measurement | `Group.Read.All`, `User.Read.All` |
