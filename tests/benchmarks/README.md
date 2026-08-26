# Mgx benchmark suite

Reproduces every number in the main README's Benchmarks section. Each script is self-contained; `run.ps1` runs the full suite and regenerates the README tables.

## Resource units

Every result now carries a `Telemetry` block alongside the timing: resource units consumed,
request counts, throttle retries, and pacing waits. Wall time alone describes half the cost -
Graph throttles directory workloads on a resource-unit budget, so two runs with identical
durations can sit very differently against that budget.

`RuPerRequest` is the figure to watch when comparing query shapes: adding `$select` measurably
lowers it. Arms that do not load Mgx (the bare-SDK comparisons) record `Telemetry: null`, since
there is no session telemetry to read - expected, not a failure.


## Prerequisites

- PowerShell 7.5+, `Microsoft.Graph.Authentication`, `Microsoft.Graph.Users`
- A Graph session (`Connect-MgGraph`) **or** app-only credentials in
  `~/.mgx-bench/app.json` (`{ tenantId, appId, clientSecret }`) — the app needs
  `User.ReadWrite.All`, `Group.ReadWrite.All`, `Directory.ReadWrite.All`, `AuditLog.Read.All`
- A seeded test tenant (~100k users, ~15k groups) for the tenant benchmarks.
  `06-fault-gauntlet.ps1` needs **no tenant** — it runs against a local mock.

Never point these scripts at a production tenant. Benchmarks 04 and 07 create and delete objects; 07 and 10 deliberately provoke throttling.

## The benchmarks

| # | Script | Claim it proves | Tenant? |
|---|--------|-----------------|---------|
| 01 | `01-list-users.ps1` | Streaming enumeration beats SDK/raw REST; time-to-first-item | yes |
| 02 | `02-fanout-lookup.ps1` | Bounded fan-out beats sequential SDK and DIY `ForEach -Parallel` | yes |
| 03 | `03-user-report.ps1` | Composite real workload (users + groups + apps) | yes |
| 04 | `04-batch-create.ps1` | Batched writes ~5× faster; cleans up after itself | yes |
| 05 | `05-memory-export.ps1` | Streaming export: flat memory, no PSObject tax | yes |
| 06 | `06-fault-gauntlet.ps1` | Resilience under controlled 429/5xx injection (local mock Graph) | no |
| 07 | `07-adaptive-pacing.ps1` | AIMD write pacing vs naive full-speed writes under real throttling | yes |
| 08 | `08-delta-sync.ps1` | Delta sync: initial pull, then incremental cost | yes |
| 09 | `09-kill-resume.ps1` | Checkpoint/resume correctness: exact count, zero duplicates | yes |
| 10 | `10-pacing-under-real-throttling.ps1` | Adaptive pacing pays for itself against genuine 429s | yes |
| 11 | `11-throttle-accuracy.ps1` | Under throttling, retrieval hinges on honoring `Retry-After` | yes |
| 12 | `12-delta-replay.ps1` | Delta enumerations repeat objects; replay factor vs ground truth | yes |
| 13 | `13-resource-unit-rate.ps1` | Sustained RU ceiling vs burst allowance, at held send rates | yes |
| 14 | `14-pacing-cold-cost.ps1` | What pacing costs a short run that finishes during the ramp | yes |
| 15 | `15-fanout-concurrency-scaling.ps1` | What `-Concurrency` buys, and whether the connection pool is the ceiling | yes |

11-15 are not in `run.ps1`. 11-13 each deliberately drive the tenant to 429s, so they run
standalone (10 also throttles on purpose, which is why `run.ps1` puts it last). 14 and 15 are
cheap and answer one question each; they are read on their own rather than as part of a sweep.

## Methodology

- Reads report the **median of 3 runs**; large write benchmarks run once and say so.
- Baselines run at their best configuration: `$top=999` + `$select` on raw REST,
  identical properties for every contender.
- Every pass records Mgx session telemetry (HTTP time vs rate-limiter wait vs
  retry delay) alongside wall time — `common.ps1 > Measure-BenchPass`.
- Memory numbers are peak working set sampled at 200ms plus managed-heap delta;
  streaming claims are measured with streaming consumers (piped, never assigned).
- Results append to `results/<benchmark>.json` with Mgx/SDK/PS versions and a
  timestamp; the README tables are generated from the latest entries.
