# Mgx
Microsoft Graph becomes difficult when a PowerShell process turns into a long-running, concurrent, stateful workload operating against a throttled and eventually consistent API. Sequential lookups are slow at scale, long-running requests can hang on dead connections, and scripts that do not explicitly handle throttling or transient failures can lose data or require manual recovery.

Given a large Graph workload, Mgx can execute it efficiently, preserve correctness, adapt to Graph's limits, survive transient failure and state changes, remain observable, and recover without forcing the caller to implement the machinery itself.

Mgx does this by adding concurrency, streaming pagination, batching, and resilience features to Microsoft Graph PowerShell while (optionally) using the existing `Connect-MgGraph` authentication context.

In benchmarks, operations that benefit from concurrency and batching were **4–5× faster** than the Microsoft Graph SDK. On plain enumeration, where there is nothing to parallelize, it comes out about level with a tuned SDK - the difference being that it needs no tuning to get there. Mgx also provides resilience for throttling, transient failures, dead connections, and interrupted long-running operations: enumerating a 100,000-user tenant while its request budget was held at the ceiling, it returned all 100,000, where a plain `Invoke-RestMethod` loop returned 1,700.¹

<sup>¹ Tested under sustained throttle waves, injected 429/503 faults, dead sockets, and `kill -9` during export. See [`tests/benchmarks/`](tests/benchmarks/).</sup>

## Quick start

```powershell
Install-Module Microsoft.Graph.Authentication   # Connect-MgGraph lives here
Install-Module Mgx
Connect-MgGraph -Scopes "User.Read.All"
Invoke-MgxRequest /users -All -Property displayName,mail
```

## Key Capabilities

* **Performance:** Streaming pagination, concurrent fan-out, and batched writes of up to 20 sub-requests per HTTP call.
* **Resilience:** Proactive rate limiting, exponential backoff with jitter, circuit breakers, adaptive per-workload request pacing, body-read timeouts, and connection recycling.
* **Operations:** Streamed JSONL exports with checkpoint/resume, delta sync token management, and resilience injection for existing Graph SDK scripts through `Enable-MgxResilience`.
* **Content:** File and media downloads with `Get-MgxContent`, whole or by byte range, over a redirect path validated against a host allowlist and followed without the bearer token.
* **Observability:** Execution metrics via `Get-MgxTelemetry`, including HTTP timing, throttle waits, retries, and resource consumption.

---

## Benchmarks

> **Environment:** Entra ID test tenant with 100k users and 15.8k groups. PowerShell 7.6.4 (.NET 10) with the Microsoft.Graph SDK, app-only via certificate. The module's supported floor is 7.4 (.NET 8), which is verified by the test suite but is not where these figures were measured. Every row below comes from one run of the suite against one tenant on one build; rows that could not be re-measured have been removed rather than carried forward.

### Performance & Throughput

| Operation                             |        Mgx | SDK (`Get-MgUser`) | Raw REST (`Invoke-RestMethod`) | Speedup vs SDK |
| ------------------------------------- | ---------: | -----------------: | -----------------------------: | -------------: |
| **List 100,000 users**                |  **47.1s** |             53.2s¹ |                          58.5s |           1.1× |
| **Look up 5,000 users by ID**         |  **98.8s** |             521.0s |                         985.3s |       **5.3×** |
| **User report** *(1k users + groups)* |  **23.9s** |             107.9s |                         205.8s |       **4.5×** |
| **Full delta enumeration** *(130,233 items)* | **145.6s** |              - |                              - |              - |

<sup>¹ Both figures are the SDK at `-PageSize 999`, which is what the benchmark runs. At the SDK's *default* page size the same enumeration takes **about 2.5-3x** as long - measured 3.0x (168.2s against 57.0s), 2.66x and 2.52x on separate runs of the same tenant, which is the spread this figure has — the practical difference is that mgx needs no tuning to be fast, not that it out-runs a tuned SDK on plain enumeration.</sup>

Graph charges resource units per request, by the shape of the query rather than by which client
sent it - so every column above spends the same: about 102 units for the enumeration, 5,002 for
the lookups, 2,003 for the report. That equality holds only because all three issue equivalent
requests - the page size noted above changes the request count, and the cost with it.
What mgx adds is not a cheaper request but a visible one: it accumulates `x-ms-resource-unit`
across the session and paces against the budget they are charged to. See
[Resource units: what queries actually cost](#resource-units-what-queries-actually-cost).

<sup>Measured from `x-ms-resource-unit`. `/users/{id}` costs 1 RU; `transitiveMembers` with `$select` and `$top` costs 3, matching the documented cost table exactly. At 5,002 RU the heaviest row above spends about 1.3% of the 8,000 RU / 10s Identity & Access budget for one application + tenant pair. The delta endpoint does not emit the header, so no figure is given.</sup>

### Resilience under throttling

Enumerating all 100,000 users of a test tenant while a second client held the application's
resource-unit budget at its ceiling. Ground truth is `/users/$count`, which the directory
serves from its own index rather than by walking pages, so it cannot inherit a pagination
fault from the thing it is being used to check.

| Contender                          |             Retrieved | Missing | Duplicates | Wall time |
| ---------------------------------- | --------------------: | ------: | ---------: | --------: |
| `Invoke-MgxRequest -All`           | **100,000** / 100,000 |       0 |          0 |    715.2s |
| `Get-MgUser -All`                  | **100,000** / 100,000 |       0 |          0 |    729.9s |
| `Invoke-RestMethod` + fixed retry  |       8,000 / 100,000 |  92,000 |          0 |    120.0s |
| `Invoke-RestMethod`, no retry      |       1,700 / 100,000 |  98,300 |          0 |      5.5s |

The bottom row finishes in five and a half seconds holding 1.7% of the tenant. Both the SDK
and Mgx return everything - the SDK's retry handler is correct here, and fifteen seconds
between them across twelve minutes is not a difference worth reading into. What separates the
table is whether a contender honors `Retry-After` at all, not which library it comes from.

Run again with the budget untouched, all four return exactly 100,000 with no duplicates.
The gap is produced by throttling, not by enumeration.

Adding `Enable-MgxResilience` to the SDK neither helps nor hurts here, because the SDK's own
retry handler already covers throttling. What it adds is not covered by this table: per-request
body-read timeouts and periodic connection recycling, so a dead socket surfaces as a retryable
error rather than an indefinite hang. The benchmark suite runs its SDK baselines inside
watchdogged child processes for exactly that reason - bare SDK cmdlets have no default
body-read timeout and have hung indefinitely during these runs.

### Delta enumerations repeat themselves

A delta run does not paginate a snapshot, so one object can arrive on several pages of the
same enumeration. Against a tenant of 15,779 groups, over 1,135 pages:

| Contender                                 | Emitted | Distinct |
| ----------------------------------------- | ------: | -------: |
| `Invoke-RestMethod` over `/groups/delta`  | 172,740 |   17,467 |
| `Sync-MgxDelta` over the same resource    | 172,740 |   17,467 |

Ten objects handed back for every one the directory holds. Mgx does not deduplicate this: it
is 126 seconds quicker over the same pages and exactly as repetitive. Deduplicating correctly
means choosing which occurrence of an id wins when a single run reports the same object both
live and `@removed`, and which occurrence is last is not knowable while streaming without
retaining every id seen. Until that is settled, dedupe downstream on `id`.

Distinct exceeds the group count because a full delta also returns tombstones for deleted
groups - 16,324 of the objects above carry `@removed`.

### What adaptive pacing costs when nothing is throttling

It depends entirely on how long the run is, and the honest answer is "nothing on a long one,
a lot on a short one".

The pacer opens cold at 4 rps and doubles each clean second until it reaches the ceiling, then
deactivates. A run long enough to finish ramping pays for the ramp once and never notices. A run
that finishes *during* it pays for the whole thing. Both measured on the 100k-user tenant:

| workload | pacing on *(default)* | `-NoAdaptivePacing` | measured by |
| --- | ---: | ---: | --- |
| 8,000 concurrent reads | 39.4s | 40.3s | [benchmark 07](tests/benchmarks/07-adaptive-pacing.ps1) |
| 50 lookups, concurrency 8, cold session | 4.4s | 1.0s | [benchmark 14](tests/benchmarks/14-pacing-cold-cost.ps1) |

The large run is free - 0 pacing activations, 0 throttle retries, and the paced run is if
anything the faster of the two. The small one costs **4-6x** depending on the day (4.6x above;
separate runs have given 4.4x and about 6x), and the reason is visible in telemetry: roughly 30
seconds of accumulated `AdaptivePacingWaitMs` spread across a fan-out that would otherwise take
one. If your work is short, interactive, and against a tenant you are not hammering,
`Set-MgxOption -NoAdaptivePacing` is the switch.

No 429s appeared at any concurrency the module permits. It caps at 128, and this tenant took
700 RU/s for 90s without refusing anything - a raw client only crosses the ceiling at around 200
requests in flight, which is past what Mgx will issue. Graph absorbed the load by inflating latency
from 113ms to ~1s instead. That is why pacing reacts to `Retry-After` and latency drift rather than
to a header that never arrives.

### Memory Efficiency & Recovery

Export 100,000 users to JSONL:

|                    | Mgx `Export-MgxCollection` | Mgx `Invoke-MgxRequest \| file` | `Invoke-RestMethod` buffer+write |
| ------------------ | -------------------------: | ------------------------------: | -------------------------------: |
| Wall time          |                      53.0s |                           47.0s |                            64.0s |
| Peak working set   |                      305MB |                       **265MB** |                        **555MB** |
| Managed heap delta |                    +19.8MB |                      **+5.0MB** |                     **+415.7MB** |

* **Low memory footprint:** streaming to a file adds **5MB** to the managed heap over the whole export, against 416MB for the buffer-then-write approach. Peak working set is 265MB against 555MB.
* **Kill-safe resume:** `Export-MgxCollection` checkpoints progress continuously. After interruption, rerunning the export resumes from the checkpoint without duplicating objects.

---

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

### Concurrent Fan-Out & Batching

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

---

## Microsoft.Graph Comparison

| Common Operation             | Standard `Microsoft.Graph`                         | `Mgx`                                                        |
| ---------------------------- | -------------------------------------------------- | ------------------------------------------------------------ |
| **Bulk Lookups**             | `$ids \| ForEach-Object { Get-MgUser -UserId $_ }` | `$ids \| Invoke-MgxRequest '/users/{id}'`                    |
| **Bulk Updates**             | `$ids \| ForEach-Object { Update-MgUser ... }`     | `$urls \| Invoke-MgxBatchRequest -Method PATCH -Body @{...}` |
| **Exporting Data**           | `$all = Get-MgUser -All; $all \| Export-Csv ...`   | `Export-MgxCollection /users -OutputFile users.jsonl`        |
| **Fault Protection**         | Written per script                                 | Built-in, or added to existing scripts with `Enable-MgxResilience` |
| **Observability**            | Timed by the caller                                | `Get-MgxTelemetry`                                           |
| **Dead Connection Handling** | Left to the default HTTP timeouts                  | Body-read timeouts + connection recycling                    |
| **Beta Endpoints**           | Requires `Microsoft.Graph.Beta`                    | `-ApiVersion beta`                                           |

---

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

---

## Cmdlets

| Cmdlet                                           | Description                                                                                               |
| ------------------------------------------------ | --------------------------------------------------------------------------------------------------------- |
| `Invoke-MgxRequest`                              | Executes Graph requests with automatic concurrency, retries, and rate limiting                            |
| `Invoke-MgxBatchRequest`                         | Batches up to 20 requests into a single HTTP POST call                                                    |
| `Export-MgxCollection`                           | Streams paginated API results directly to JSONL with checkpointing                                        |
| `Expand-MgxRelation`                             | Performs concurrent fan-out lookups to expand related object attributes                                   |
| `Sync-MgxDelta`                                  | Manages stateful delta queries and state token storage, with mid-run crash resume                         |
| `Get-MgxContent`                                 | Downloads content bytes (whole or ranged) with a token-free, host-validated download path                 |
| `Set-MgxOption` / `Get-MgxOption`                | Configures global limits, retry counts, timeouts, and circuit-breaker thresholds                          |
| `Enable-MgxResilience` / `Disable-MgxResilience` | Injects or removes Mgx resilience policies from native Microsoft.Graph SDK cmdlets                        |
| `Get-MgxTelemetry`                               | Outputs execution statistics including HTTP duration, rate-limit delays, retry counts, and resource units |

---

## Resilience Architecture

All HTTP operations pass through four layered [Polly 8.x](https://github.com/App-vNext/Polly) resilience strategies:

1. **Rate limiter:** Token bucket with 200 burst capacity and 50 requests/sec refill.
2. **Retry policy:** Up to 7 retries with exponential backoff and jitter for transient errors (`429`, `500`, `502`, `503`, `504`), honoring `Retry-After`.
3. **Circuit breaker:** Opens when error rates exceed 10%, with a half-open probe after 15 seconds.
4. **Timeout:** 30-second per-request limit and 300-second cumulative ceiling across retries.

### Resource units: what queries actually cost

Graph throttles directory workloads on a **resource-unit budget**, not on request count or bandwidth. Mgx reads `x-ms-resource-unit` on every response and accumulates it, so `Get-MgxTelemetry` reports what a session actually spent - see [examples/resilience-and-telemetry/resource-unit-budgeting.ps1](examples/resilience-and-telemetry/resource-unit-budgeting.ps1).

Measured against a 15,779-group test tenant:

| Query shape | RU |
|---|---|
| `transitiveMembers?$top=5` | 4 |
| `transitiveMembers?$top=5&$select=id` | **3** |
| `groups/{id}` (single read) | 1 |

Both figures match the [documented cost table](https://learn.microsoft.com/en-us/graph/throttling-limits) exactly: `transitiveMembers` is published at 5 RU, `$select` takes one off, and `$top` under 20 takes another. Cost is a property of the query *shape*, not the number of objects returned. On a per-group fan-out across that tenant, `$select` alone is the difference between 47,337 and 63,116 resource units.

Three findings from pushing a single client until the tenant pushed back:

- **The budget behaves like a token bucket, and burst hides its rate.** The published limit for a tenant of this size is 8,000 RU per 10 s per *application + tenant pair* (800 RU/s). Held at a fixed rate for 90 s from a single client: 300 and 700 RU/s were never refused - both spending well past the bucket - while 1,200 RU/s began refusing after 19 s, and once saturated the tenant served about **730 RU/s** however hard it was pushed. A short run at any of those rates shows nothing - the bucket starts full, so roughly the first 20,000 RU are free regardless of how fast they are spent, which is why a burst measurement reads as a much higher sustained ceiling than the tenant actually has. The ceiling itself moves: the day before, the same probe was refused at 631 RU/s and saturated near 440. Re-measure with [`tests/benchmarks/13-resource-unit-rate.ps1`](tests/benchmarks/13-resource-unit-rate.ps1) rather than trusting these constants.
- **`x-ms-throttle-limit-percentage` was never emitted** - not at 1.5x the documented budget, and not while the tenant was actively returning 429s. Mgx therefore treats it as opportunistic and paces on 429 + `Retry-After` and latency drift, which are reliable.
- **Batching does not buy cheaper units.** Unbatched requests sustained a *higher* RU/s before throttling than the same requests inside `$batch`. Batching saves round-trips, not budget.

Limits are scoped per **application + tenant pair**, and separately per service - Intune, Excel, Education and the rest each carry their own quota. That is why pacing state is partitioned by workload rather than pooled: a throttled directory fan-out says nothing about the budget remaining for Teams.

A failed request costs 0 RU - so a fan-out that fails uniformly consumes no budget, triggers no throttling, and finishes fast while measuring nothing. Check status codes, not duration.

Ahead of those four, an **adaptive pacing gate** spaces requests before they are sent. It is on by default and spaces requests on both the `Invoke-Mgx*` path and the `Enable-MgxResilience` SDK-bridge path. It learns a per-workload rate from throttling signals (additive increase, multiplicative decrease), keeps Drive, Directory and other workloads in separate buckets so one throttled workload does not slow the rest, and starts conservatively on a cold session. The learning happens only on the `Invoke-Mgx*` path: the SDK's own retry handler sits inside the wrapped chain and answers a 429 before the pipeline sees it, so a bridged session keeps spacing but never adapts. Batch outer POSTs are the one exemption - `GraphBatchClient` runs its own item-level AIMD, and stacking two controllers on one workload would compound their backoff. Disable with `Set-MgxOption -NoAdaptivePacing`; inspect the learned state in `Get-MgxTelemetry`.

---

## Requirements & Building

### Requirements

* **PowerShell:** 7.4+
* **Authentication:** `Microsoft.Graph.Authentication` 2.10.0+ for token acquisition. It is not a declared module dependency.

### Build from Source

```powershell
./build.ps1
```

Dependencies (`Microsoft.Graph.Core`, `Polly.Core`, and `System.Threading.RateLimiting`) are loaded into an isolated **Assembly Load Context** to avoid version conflicts with other PowerShell modules.

---

## License

[MIT](LICENSE)
