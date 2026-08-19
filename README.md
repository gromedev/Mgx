# Mgx

Microsoft Graph PowerShell works well for ordinary scripting, but high-volume operations can become difficult to handle efficiently. Sequential lookups are slow at scale, long-running requests can hang on dead connections, and scripts that do not explicitly handle throttling or transient failures can lose data or require manual recovery.

Mgx adds concurrency, streaming pagination, batching, and resilience features to Microsoft Graph PowerShell while using the existing `Connect-MgGraph` authentication context.

In benchmarks, operations that benefit from concurrency and batching were **5–12× faster** than the Microsoft Graph SDK. Mgx also provides resilience for throttling, transient failures, dead connections, and interrupted long-running operations. In a 24-hour fault-injection test against a 100,000-user tenant, Mgx completed every operation without losing a single object.

<sup>¹ Tested under sustained throttle waves, injected 429/503 faults, dead sockets, and `kill -9` during export. See [`tests/benchmarks/`](tests/benchmarks/).</sup>

## Quick start

```powershell
Install-Module Mgx
Connect-MgGraph -Scopes "User.Read.All"
Invoke-MgxRequest /users -All -Property displayName,mail
```

## Key Capabilities

* **Performance:** Streaming pagination, concurrent fan-out, and batched writes of up to 20 sub-requests per HTTP call.
* **Resilience:** Proactive rate limiting, exponential backoff with jitter, circuit breakers, adaptive per-workload request pacing, body-read timeouts, and connection recycling.
* **Operations:** Streamed JSONL exports with checkpoint/resume, delta sync token management, and resilience injection for existing Graph SDK scripts through `Enable-MgxResilience`.
* **Observability:** Execution metrics via `Get-MgxTelemetry`, including HTTP timing, throttle waits, retries, and resource consumption.

---

## Benchmarks

> **Environment:** Entra ID test tenant with 100k users and 15.8k groups. PowerShell 7.6.4 (.NET 10) with the Microsoft.Graph SDK, app-only via certificate. The module's supported floor is 7.4 (.NET 8), which is verified by the test suite but is not where these figures were measured. Every row below comes from one run of the suite against one tenant on one build; rows that could not be re-measured have been removed rather than carried forward.

### Performance & Throughput

| Operation                             |        Mgx | SDK (`Get-MgUser`) | Raw REST (`Invoke-RestMethod`) | Speedup vs SDK | Resource units |
| ------------------------------------- | ---------: | -----------------: | -----------------------------: | -------------: | -------------: |
| **List 100,000 users**                |  **47.1s** |             53.2s¹ |                         58.5s |          1.1× |         ~102 |
| **Look up 5,000 users by ID**         |  **98.8s** |             521.0s |                        985.3s |      **5.3×** |       ~5,002 |
| **User report** *(1k users + groups)* |  **23.9s** |             107.9s |                        205.8s |      **4.5×** |       ~2,003 |
| **Full delta enumeration** *(130,233 items)* | **145.6s** |                  - |                             - |             - |            - |

<sup>¹ Both figures are the SDK at `-PageSize 999`, which is what the benchmark runs. At the SDK's *default* 100-item page size the same enumeration takes roughly ten times as long — the practical difference is that mgx needs no tuning to be fast, not that it out-runs a tuned SDK on plain enumeration.</sup>

<sup>Resource units are per single operation, measured from `x-ms-resource-unit`, and are a property of the query shape rather than the client. `/users/{id}` costs 1 RU; `transitiveMembers` with `$select` and `$top` costs 3, matching the documented cost table exactly. At 5,002 RU the heaviest row above spends about 1.3% of the 8,000 RU / 10s Identity &amp; Access budget for one application + tenant pair. The delta endpoint does not emit the header, so no figure is given.</sup>

### Resilience Under Faults & Throttling

**Fault injection:** 15% `429` throttling and 3% `503` errors across 1,000 lookups.

* **Mgx:** Completed **1,000 / 1,000** in **132.8s** with 0 failures.
* **Bare SDK:** Completed **1,000 / 1,000** in **211.8s**.
* **Naive REST:** Completed in **1.0s**, but silently dropped **180 items**.
* **SDK + `Enable-MgxResilience`:** Completed **1,000 / 1,000** in **211.6s** - the SDK's own retry handler already covers this fault profile, so injection neither helps nor hurts here.
* **Connection recovery:** Per-request body-read timeouts and periodic connection recycling prevent long-running operations from remaining stuck on dead sockets.

Read the mgx figure with its cause in view. This harness injects `429`s on a fixed schedule keyed
on entity id, so the fault rate is **independent of how fast you send**. Adaptive pacing responds to
those throttle signals by spacing subsequent requests - 447s of accumulated wait across 994 of 2,000
requests - and on a mock whose faults cannot be avoided, that spacing is pure cost. It is the one
condition where backing off can buy nothing.

Against a real tenant the pacer behaves differently, because it only constrains while ramping from
cold or while backing off from an observed throttle. Once slow start reaches the configured ceiling
it deactivates, and with no throttling there is no adapted cap, so the gate is inert. Measured on the
100k-user tenant, 8,000 concurrent reads with pacing on and off:

| | wall | throttle retries | pacing wait |
| --- | ---: | ---: | ---: |
| Adaptive pacing on *(default)* | 39.4s | 0 | 0s, 0 activations |
| `-NoAdaptivePacing` | 40.3s | 0 | - |

No 429s appeared at any concurrency the module permits; Graph absorbed the load by inflating latency
from 113ms to ~1s instead. That is consistent with the throttling behaviour documented below, and it
is why pacing reacts to `Retry-After` and latency drift rather than to a header that never arrives.

### Memory Efficiency & Recovery

Export 100,000 users to JSONL:

|                    | Mgx `Export-MgxCollection` | Mgx `Invoke-MgxRequest \| file` | `Invoke-RestMethod` buffer+write |
| ------------------ | -------------------------: | ------------------------------: | -------------------------------: |
| Wall time          |                      74.0s |                           76.0s |                           157.2s |
| Peak working set   |                      341MB |                       **225MB** |                        **504MB** |
| Managed heap delta |                    +19.8MB |                      **+8.0MB** |                     **+428.9MB** |

* **Low memory footprint:** JSONL export peaks as low as **225MB RAM**, compared with 504MB for buffered REST.
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
| **Fault Protection**         | Manual script handling required                    | Built-in or via `Enable-MgxResilience`                       |
| **Observability**            | No retry/throttle metrics                          | `Get-MgxTelemetry`                                           |
| **Dead Connection Handling** | Indefinite hangs possible                          | Body-read timeouts + connection recycling                    |
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

- **The budget behaves like the documented token bucket.** The published limit for a tenant of this size is 8,000 RU per 10 s per *application + tenant pair* (800 RU/s). A single client sustained **882 RU/s with zero throttling** - about 10 % over, consistent with bucket burst capacity draining - and the first 429s appeared around **1,200 RU/s**, roughly 50 % over.
- **`x-ms-throttle-limit-percentage` was never emitted** - not at 1.5x the documented budget, and not while the tenant was actively returning 429s. Mgx therefore treats it as opportunistic and paces on 429 + `Retry-After` and latency drift, which are reliable.
- **Batching does not buy cheaper units.** Unbatched requests sustained a *higher* RU/s before throttling than the same requests inside `$batch`. Batching saves round-trips, not budget.

Limits are scoped per **application + tenant pair**, and separately per service - Intune, Excel, Education and the rest each carry their own quota. That is why pacing state is partitioned by workload rather than pooled: a throttled directory fan-out says nothing about the budget remaining for Teams.

A failed request costs 0 RU - so a fan-out that fails uniformly consumes no budget, triggers no throttling, and finishes fast while measuring nothing. Check status codes, not duration.

Ahead of those four, an **adaptive pacing gate** spaces requests before they are sent. It is on by default and applies to *every* request, on both the `Invoke-Mgx*` path and the `Enable-MgxResilience` SDK-bridge path. It learns a per-workload rate from throttling signals (additive increase, multiplicative decrease), keeps Drive, Directory and other workloads in separate buckets so one throttled workload does not slow the rest, and starts conservatively on a cold session. Batch outer POSTs are the one exemption - `GraphBatchClient` runs its own item-level AIMD, and stacking two controllers on one workload would compound their backoff. Disable with `Set-MgxOption -NoAdaptivePacing`; inspect the learned state in `Get-MgxTelemetry`.

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
