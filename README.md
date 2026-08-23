# Mgx

Microsoft Graph works well for ordinary scripts. It gets harder when a script becomes a long-running job making thousands of requests.

At that point, the problems are usually not authentication or the Graph API itself. They are pagination, throttling, concurrency, dead connections, retries, memory usage, and jobs that need to survive for hours without manual intervention.

Mgx is a PowerShell client for Microsoft Graph that deals with those problems. It adds concurrent requests, streaming pagination, batching, rate limiting, retries, connection recovery, checkpointed exports, delta synchronization, and telemetry. It can use the authentication context established by `Connect-MgGraph`, so it does not require a separate authentication system.

In benchmarks against a 100,000-user tenant, workloads involving many individual lookups were **4–5× faster** than the Microsoft Graph SDK. Plain enumeration, where there is little to parallelize, is roughly the same speed as a tuned SDK.

The bigger difference is what happens when Graph starts throttling or the network stops cooperating. In one test, a 100,000-user enumeration was run while the application's request budget was held at its limit. Mgx returned all 100,000 users. A raw `Invoke-RestMethod` loop without equivalent retry and pacing logic returned only 1,700.¹

<sup>¹ Tested with sustained throttle waves, injected 429/503 responses, dead sockets, and process termination during export. See [`tests/benchmarks/`](tests/benchmarks/).</sup>

## Quick start

```powershell
Install-Module Microsoft.Graph.Authentication   # Connect-MgGraph lives here
Install-Module Mgx
Connect-MgGraph -Scopes "User.Read.All"
Invoke-MgxRequest /users -All -Property displayName,mail
```

## What Mgx adds

- Concurrent fan-out for workloads with many independent requests
- Streaming pagination instead of collecting the entire result set in memory
- Batch requests with up to 20 sub-requests per HTTP call
- Proactive rate limiting, retries with jitter, circuit breaking, and connection recycling
- Adaptive per-workload request pacing to avoid running into Graph limits unnecessarily
- JSONL exports with checkpoints and resume support
- Delta synchronization with persisted state and mid-run recovery
- Whole-file and ranged content downloads through a validated redirect path without forwarding the bearer token
- Telemetry for HTTP timing, throttling, retries, and resource consumption
- `Enable-MgxResilience` for adding Mgx's resilience handling to existing Microsoft Graph SDK scripts

## Benchmarks

> **Environment:** Entra ID test tenant with 100k users and 15.8k groups. PowerShell 7.6.4 (.NET 10) with the Microsoft.Graph SDK, app-only via certificate. The module's supported floor is 7.4 (.NET 8), which is verified by the test suite but is not where these figures were measured. Every row below comes from one run of the suite against one tenant on one build; rows that could not be re-measured have been removed rather than carried forward.

### Performance & throughput

| Operation | Mgx | SDK (`Get-MgUser`) | Raw REST (`Invoke-RestMethod`) | Speedup vs SDK |
| --- | ---: | ---: | ---: | ---: |
| **List 100,000 users** | **47.1s** | 53.2s¹ | 58.5s | 1.1× |
| **Look up 5,000 users by ID** | **98.8s** | 521.0s | 985.3s | **5.3×** |
| **User report** *(1k users + groups)* | **23.9s** | 107.9s | 205.8s | **4.5×** |
| **Full delta enumeration** *(130,233 items)* | **145.6s** | - | - | - |

<sup>¹ Both figures are the SDK at `-PageSize 999`, which is what the benchmark runs. At the SDK's default page size the same enumeration takes about 2.5–3× as long. The practical difference is that Mgx needs no tuning to get close to the tuned SDK result.</sup>

Graph charges resource units per request based on the query shape, not on which client sent it. The three columns above therefore spend roughly the same amount when they issue equivalent requests: about 102 units for the enumeration, 5,002 for the lookups, and 2,003 for the report.

Mgx does not make those requests cheaper. It makes the budget visible: it accumulates `x-ms-resource-unit` across the session and uses that information when pacing requests. See [Resource units: what queries actually cost](#resource-units-what-queries-actually-cost).

<sup>Measured from `x-ms-resource-unit`. `/users/{id}` costs 1 RU; `transitiveMembers` with `$select` and `$top` costs 3, matching the documented cost table. At 5,002 RU the heaviest row above spends about 1.3% of the 8,000 RU / 10s Identity & Access budget for one application + tenant pair. The delta endpoint does not emit the header, so no figure is given.</sup>

### Resilience under throttling

This test enumerated all 100,000 users while a second client held the application's resource-unit budget at its ceiling. Ground truth is `/users/$count`, which the directory serves from its own index rather than by walking pages.

| Contender | Retrieved | Missing | Duplicates | Wall time |
| --- | ---: | ---: | ---: | ---: |
| `Invoke-MgxRequest -All` | **100,000 / 100,000** | 0 | 0 | 715.2s |
| `Get-MgUser -All` | **100,000 / 100,000** | 0 | 0 | 729.9s |
| `Invoke-RestMethod` + fixed retry | 8,000 / 100,000 | 92,000 | 0 | 120.0s |
| `Invoke-RestMethod`, no retry | 1,700 / 100,000 | 98,300 | 0 | 5.5s |

The raw REST clients finish sooner because they stop making useful progress as soon as the budget is exhausted. Mgx and the SDK take longer because they keep going until the enumeration is complete. The SDK's retry handling is already correct here; the useful distinction is whether the client actually honors throttling instead of treating the first wave of 429s as the end of the job.

Running the same enumeration without the competing load produced exactly 100,000 users from all four clients. The difference above is caused by throttling, not pagination.

`Enable-MgxResilience` does not replace the SDK's existing retry handler. The SDK already handles throttling. Mgx adds things that are not covered by that handler, including per-request body-read timeouts and periodic connection recycling, so a dead socket can become a retryable failure instead of an indefinite hang.

### Delta enumerations repeat themselves

A delta run does not paginate a snapshot. The same object can appear on several pages of one enumeration. Against a tenant containing 15,779 groups, over 1,135 pages produced these results:

| Contender | Emitted | Distinct |
| --- | ---: | ---: |
| `Invoke-RestMethod` over `/groups/delta` | 172,740 | 17,467 |
| `Sync-MgxDelta` over the same resource | 172,740 | 17,467 |

Mgx is not silently deduplicating these objects. Correct deduplication requires a policy for cases where the same ID appears both live and as `@removed`, and the final occurrence is not knowable while streaming without retaining every ID seen. Until a policy is defined by the caller, dedupe downstream on `id`.

Distinct exceeds the group count because a full delta also returns tombstones for deleted groups; 16,324 of the objects above carry `@removed`.

### What adaptive pacing costs when nothing is throttling

Adaptive pacing is enabled by default. It starts conservatively, increases the allowed rate as requests succeed, and backs off when throttling or latency changes indicate that the workload is approaching a limit.

That has almost no cost on a long-running workload because the initial ramp is paid once. It can be noticeable on a small workload that finishes before the ramp is complete.

| Workload | Pacing on *(default)* | `-NoAdaptivePacing` | Measured by |
| --- | ---: | ---: | --- |
| 8,000 concurrent reads | 39.4s | 40.3s | [benchmark 07](tests/benchmarks/07-adaptive-pacing.ps1) |
| 50 lookups, concurrency 8, cold session | 4.4s | 1.0s | [benchmark 14](tests/benchmarks/14-pacing-cold-cost.ps1) |

The large run had zero pacing activations and zero throttle retries; the paced run was slightly faster. The small run paid roughly 4–6× because the workload completed during the cold-start ramp. Telemetry exposes this as `AdaptivePacingWaitMs`.

For short interactive workloads against a tenant you are not pushing hard, disable it with:

```powershell
Set-MgxOption -NoAdaptivePacing
```

No 429s appeared at any concurrency the module permits. It caps at 128, and this tenant sustained roughly 700 RU/s for 90 seconds without refusing requests. A raw client only crossed the tenant's ceiling at around 200 requests in flight, which is beyond what Mgx will issue. Graph instead responded by increasing latency from about 113 ms to roughly 1 second. The pacing logic therefore reacts to `Retry-After` and latency drift rather than waiting for a throttle header that may never arrive.

### Memory efficiency & recovery

Exporting 100,000 users to JSONL:

| | Mgx `Export-MgxCollection` | Mgx `Invoke-MgxRequest \| file` | `Invoke-RestMethod` buffer+write |
| --- | ---: | ---: | ---: |
| Wall time | 53.0s | 47.0s | 64.0s |
| Peak working set | 305MB | **265MB** | **555MB** |
| Managed heap delta | +19.8MB | **+5.0MB** | **+415.7MB** |

Streaming directly to a file added 5MB to the managed heap over the whole export, compared with 416MB for the buffer-then-write approach. Peak working set was 265MB versus 555MB.

`Export-MgxCollection` also checkpoints progress continuously. After an interruption, rerunning the export resumes from the checkpoint without duplicating objects.

## Examples

```powershell
# All users, streamed
Invoke-MgxRequest /users -All -Property displayName,mail,department

# Filter
Invoke-MgxRequest /users -Filter "department eq 'Engineering'" -Property displayName,mail

# Single user
Invoke-MgxRequest "/users/$userId"

# Pipe to CSV
Invoke-MgxRequest /users -All -Property displayName,mail,department |
    Select-Object displayName,mail,department |
    Export-Csv users.csv

# JSONL export with checkpoint/resume
Export-MgxCollection /auditLogs/signIns -OutputFile ./signins.jsonl -CheckpointPath ./cp.json -All

# Beta endpoints
Invoke-MgxRequest /users -ApiVersion beta -Top 10

# Add resilience to existing SDK scripts
Enable-MgxResilience
Get-MgUser -All
Disable-MgxResilience
```

### Concurrent fan-out & batching

```powershell
# Concurrent fan-out lookups
@("id1", "id2", "id3") | Invoke-MgxRequest '/users/{id}'

# Resolve nested relationships in parallel
Invoke-MgxRequest /users -Top 50 |
    Expand-MgxRelation '/users/{id}/manager' -As Manager -Flatten

# Batch up to 20 requests per HTTP call
@("/users/id1", "/users/id2") |
    Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "HR" }

# Delta sync with automatic token handling
Sync-MgxDelta /users/delta -DeltaPath ./delta.json -Property displayName,mail

# Enumerate a whole drive, resumable mid-run; later runs return only changes
Sync-MgxDelta /me/drive/root/delta -DeltaPath ./drive.json -CheckpointPath ./drive.cp -OutputFile drive.jsonl

# Ranged reads: first 256 KB of each file instead of the whole thing
Invoke-MgxRequest "/me/drive/items/$folderId/children" -All |
    Where-Object { $_.file } |
    Get-MgxContent -First 262144
```

See [`examples/`](examples/) for additional examples.

## Microsoft.Graph comparison

| Common operation | Standard `Microsoft.Graph` | `Mgx` |
| --- | --- | --- |
| **Bulk lookups** | `$ids \| ForEach-Object { Get-MgUser -UserId $_ }` | `$ids \| Invoke-MgxRequest '/users/{id}'` |
| **Bulk updates** | `$ids \| ForEach-Object { Update-MgUser ... }` | `$urls \| Invoke-MgxBatchRequest -Method PATCH -Body @{...}` |
| **Exporting data** | `$all = Get-MgUser -All; $all \| Export-Csv ...` | `Export-MgxCollection /users -OutputFile users.jsonl` |
| **Fault protection** | Written per script | Built in, or added to existing scripts with `Enable-MgxResilience` |
| **Observability** | Timed by the caller | `Get-MgxTelemetry` |
| **Dead connection handling** | Left to the default HTTP timeouts | Body-read timeouts + connection recycling |
| **Beta endpoints** | Requires `Microsoft.Graph.Beta` | `-ApiVersion beta` |

## Output shape

Graph-data cmdlets emit case-insensitive `Hashtable`s, matching the shape returned by `Invoke-MgGraphRequest`:

```powershell
$user = Invoke-MgxRequest "/users/$id"

$user.displayName
$user['@odata.type']
```

`@odata.type` is the only annotation preserved. Other `@odata.*` transport metadata is stripped, including `@odata.etag`. Use `-Raw` when the original payload or `If-Match` tag is required.

Hashtable keys have no defined order. Pin columns explicitly when output order matters:

```powershell
Invoke-MgxRequest /users -All |
    Select-Object displayName,mail |
    Export-Csv users.csv
```

To work with `PSCustomObject`s instead, request the raw payload:

```powershell
Invoke-MgxRequest /users -All -Raw | ConvertFrom-Json
```

## Cmdlets

| Cmdlet | Description |
| --- | --- |
| `Invoke-MgxRequest` | Executes Graph requests with automatic concurrency, retries, and rate limiting |
| `Invoke-MgxBatchRequest` | Batches up to 20 requests into a single HTTP POST call |
| `Export-MgxCollection` | Streams paginated API results directly to JSONL with checkpointing |
| `Expand-MgxRelation` | Performs concurrent fan-out lookups to expand related object attributes |
| `Sync-MgxDelta` | Manages stateful delta queries and state token storage, with mid-run crash resume |
| `Get-MgxContent` | Downloads content bytes, whole or ranged, through a token-free validated download path |
| `Set-MgxOption` / `Get-MgxOption` | Configures global limits, retry counts, timeouts, and circuit-breaker thresholds |
| `Enable-MgxResilience` / `Disable-MgxResilience` | Injects or removes Mgx resilience policies from native Microsoft.Graph SDK cmdlets |
| `Get-MgxTelemetry` | Outputs execution statistics including HTTP duration, rate-limit delays, retry counts, and resource units |

## Resilience architecture

All HTTP operations pass through four layered [Polly 8.x](https://github.com/App-vNext/Polly) resilience strategies:

1. **Rate limiter:** Token bucket with 200 burst capacity and 50 requests/sec refill.
2. **Retry policy:** Up to 7 retries with exponential backoff and jitter for transient errors (`429`, `500`, `502`, `503`, `504`), honoring `Retry-After`.
3. **Circuit breaker:** Opens when error rates exceed 10%, with a half-open probe after 15 seconds.
4. **Timeout:** 30-second per-request limit and 300-second cumulative ceiling across retries.

### Resource units: what queries actually cost

Graph throttles directory workloads on a **resource-unit budget**, not simply on request count or bandwidth. Mgx reads `x-ms-resource-unit` from responses and accumulates it, so `Get-MgxTelemetry` can show what a session actually spent. See [examples/resilience-and-telemetry/resource-unit-budgeting.ps1](examples/resilience-and-telemetry/resource-unit-budgeting.ps1).

Measured against a 15,779-group test tenant:

| Query shape | RU |
| --- | ---: |
| `transitiveMembers?$top=5` | 4 |
| `transitiveMembers?$top=5&$select=id` | **3** |
| `groups/{id}` (single read) | 1 |

Both figures match the documented cost table: `transitiveMembers` is published at 5 RU, `$select` takes one off, and `$top` under 20 takes another. Cost is a property of the query shape, not the number of objects returned. On a per-group fan-out across that tenant, `$select` alone is the difference between 47,337 and 63,116 resource units.

Three findings from pushing a single client until the tenant pushed back:

- **The budget behaves like a token bucket, and burst hides its rate.** The published limit for a tenant of this size is 8,000 RU per 10 s per *application + tenant pair* (800 RU/s). Held at a fixed rate for 90 s from a single client, 300 and 700 RU/s were never refused. 1,200 RU/s began refusing after 19 s, and once saturated the tenant served about 730 RU/s however hard it was pushed. The exact ceiling moves: the same probe was refused at 631 RU/s on the previous day and saturated near 440. Re-measure with [`tests/benchmarks/13-resource-unit-rate.ps1`](tests/benchmarks/13-resource-unit-rate.ps1) rather than treating these measurements as constants.
- **`x-ms-throttle-limit-percentage` was never emitted.** Mgx therefore treats it as opportunistic and paces on 429 + `Retry-After` and latency drift instead.
- **Batching does not buy cheaper units.** Unbatched requests sustained a higher RU/s before throttling than the same requests inside `$batch`. Batching saves round-trips, not budget.

Limits are scoped per **application + tenant pair**, and separately per service. That is why pacing state is partitioned by workload rather than pooled: a throttled directory fan-out says nothing about the budget remaining for Teams.

A failed request costs 0 RU, so a fan-out that fails uniformly consumes no budget, triggers no throttling, and finishes fast while measuring nothing. Check status codes, not duration.

Adaptive pacing runs ahead of the four resilience strategies above. It spaces requests before they are sent, learns a per-workload rate from throttling signals using additive increase / multiplicative decrease, and keeps Drive, Directory, and other workloads in separate buckets. It starts conservatively on a cold session.

Batch outer POSTs are the one exemption. `GraphBatchClient` runs its own item-level AIMD controller; stacking two controllers on one workload would compound their backoff.

Disable adaptive pacing with `Set-MgxOption -NoAdaptivePacing` and inspect the learned state in `Get-MgxTelemetry`.

## Requirements & building

### Requirements

- **PowerShell:** 7.4+
- **Authentication:** `Microsoft.Graph.Authentication` 2.10.0+ for token acquisition. It is not a declared module dependency.

### Build from source

```powershell
./build.ps1
```

Dependencies (`Microsoft.Graph.Core`, `Polly.Core`, and `System.Threading.RateLimiting`) are loaded into an isolated **Assembly Load Context** to avoid version conflicts with other PowerShell modules.

## License

[MIT](LICENSE)
