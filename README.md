# Mgx

Microsoft Graph PowerShell works well for ordinary scripting, but high-volume operations can become difficult to handle efficiently. Sequential lookups are slow at scale, long-running requests can hang on dead connections, and scripts that do not explicitly handle throttling or transient failures can lose data or require manual recovery.

Mgx adds concurrency, streaming pagination, batching, and resilience features to Microsoft Graph PowerShell scripts while using the existing Connect-MgGraph authentication context.

Compared to SDK and REST, Mgx is about **5–12× faster** wherever concurrency and batching apply. But Mgx's **resilience** is what makes it enterprise hardend. Across a 24 hour session of hostile  on a 100,000 user tenant **Mgx completed every operation without losing a single object**.

SDK and REST didn't even come close. Even at much lower scales. Which is what drove Mgx to being developed in the first place.

<sub>¹ i.e. sustained throttle waves, injected 429/503 faults, dead sockets, `kill -9` mid-export. Try for yourself heree [`tests/benchmarks/`](tests/benchmarks/).</sub> 

## Quick start

```powershell
Install-Module Mgx
Connect-MgGraph -Scopes "User.Read.All"
Invoke-MgxRequest /users -All -Property displayName,mail
```

## Key Capabilities

- **Speed:** Streaming pagination, concurrent fan-out, and batched writes (up to 20 sub-requests per HTTP call).
- **Resilience:** Proactive rate limiting, exponential backoff retries with jitter, circuit breakers, adaptive write pacing, body-read timeouts, and connection recycling.
- **Operations:** Streamed JSONL exports with kill-safe checkpoint/resume, delta sync token management, and transparent SDK resilience injection (`Enable-MgxResilience`).
- **Observability:** Granular execution metrics via `Get-MgxTelemetry` (HTTP timing, throttle waits, retries, resource consumption).

---

## Benchmarks

> **Environment:** Entra ID test tenant (100k users, 15.4k groups, 705 service principals) on PowerShell 7.5 & Microsoft.Graph SDK 2.34.

### Performance & Throughput

| Operation | Mgx | SDK (`Get-MgUser`) | Raw REST (`Invoke-RestMethod`) | Speedup vs SDK |
|-----------|-----|--------------------|--------------------------------|----------------|
| **List 100,000 users** | **78.2s** | 505.0s¹ | 153.3s | **6.5× faster**|
| **Look up 5,000 users by ID** | **422.0s** | 2,183.0s | 5,500s² | **5.2× faster** |
| **User report** *(1k users + groups)* | **87.8s** | 450.0s | 1,132.0s | **5.1× faster** |
| **Create 10,000 users** | **510.0s** | 6,158.0s | Failed *(Dead socket hang)* | **12.1× faster** |
| **Recurring delta sync** *(200 changes)* | **0.9s** | 84.5s *(Full re-pull)* | Manual | **94× less work** |



<sub>¹ SDK shown at its default 100-item pages: how virtually every script runs it. Manually passing `-PageSize 999` (a parameter almost nobody knows exists) improves it to 84.5s; Mgx is still 1.1× faster, with nothing to know or tune.</sub>  
<sub>² single run completed 3,472 of 5,000: the single-token naive script's token expired mid-run; three attempts at this baseline failed three different ways, which is rather the point.</sub>  

---

### Resilience Under Faults & Throttling

- **Fault Gauntlet (15% 429 throttling / 3% 503 errors across 1,000 lookups)**
- **Mgx:** Completed **1,000 / 1,000** in **43.2s** (0 failures).
- **Bare SDK:** Completed **1,000 / 1,000** in **211.7s** (sequential waiting).
- **Naive REST:** Finished in **0.9s** but **silently dropped 180 items (18% data loss)**.
- **SDK + `Enable-MgxResilience`:** Achieved 100% completion at wall time identical to the bare SDK — the injected resilience costs nothing until something goes wrong.
- **Adaptive Write Pacing:** Dynamically scales the write rate (20 → 10 → back to 20 items/sec) during throttling waves. In a 77,000-object seeding campaign, Mgx executed 3,710 requests with **0 lost objects** and **0 circuit-breaker trips**.
- **Connection Recovery:** Enforces per-request body-read timeouts and recycles pooled HTTP connections every 2 minutes to prevent socket hangs.

---

### Memory Efficiency & Recovery

Export 100,000 users to JSONL:

| | Mgx `Export-MgxCollection` | Mgx `Invoke-MgxRequest \| file` | `Invoke-RestMethod` buffer+write |
|---|---------------------------|--------------------------------|----------------------------------|
| Wall time | 74.0s | 76.0s | 157.2s |
| Peak working set | 341MB | 225MB | **504MB** |
| Managed heap delta | +19.8MB | **+8.0MB** | **+428.9MB** |


- **Low Memory Footprint:** Exporting 100,000 users to JSONL peaks as low as **225MB RAM** (vs. 504MB for buffered REST), leveraging continuous GC reclamation.
- **Kill-Safe Resume:** `Export-MgxCollection` checkpoints progress continuously. If interrupted (`SIGKILL`), re-running recovers from the temp file checkpoint and resumes without duplicates (0.2% overhead).

---

### Production Scale

- **Bulk Deletes:** Processed 87,000 user deletions in a 300k-object tenant in **4.8 hours unattended** with zero failures.
- **High Volume Reads:** Executed tens of thousands of benchmark requests against a live tenant with **0 failed requests** and 0 circuit-breaker trips.

---

## Examples
```powershell
# All users, streamed
Invoke-MgxRequest /users -All -Property displayName,mail,department

# Filter
Invoke-MgxRequest /users -Filter "department eq 'Engineering'" -Property displayName,mail

# Single user
Invoke-MgxRequest "/users/$userId"

# Pipe straight to CSV
Invoke-MgxRequest /users -All -Property displayName,mail,department | Export-Csv users.csv

# JSONL export with checkpoint/resume (survives Ctrl+C)
Export-MgxCollection /auditLogs/signIns -OutputFile ./signins.jsonl -CheckpointPath ./cp.json -All

# Beta endpoints, no extra module
Invoke-MgxRequest /users -ApiVersion beta -Top 10

# Add resilience to existing SDK scripts
Enable-MgxResilience
Get-MgUser -All                  # now has retry, circuit breaker, rate limiting
Disable-MgxResilience            # back to normal
```

### Concurrent Fan-Out & Batching

```powershell
# Concurrent fan-out lookups
@("id1", "id2", "id3") | Invoke-MgxRequest '/users/{id}'

# Resolve nested relationships in parallel
Invoke-MgxRequest /users -Top 50 | Expand-MgxRelation '/users/{id}/manager' -As Manager -Flatten

# Batch up to 20 requests per HTTP call
@("/users/id1", "/users/id2") | Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "HR" }

# Delta sync with automatic token handling
Sync-MgxDelta /users/delta -DeltaPath ./delta.json -Property displayName,mail
```

23 more examples in [`examples/`](examples/).

---

## Microsoft.Graph Comparison

| Common Operation | Standard `Microsoft.Graph` | `Mgx` |
|------------------|---------------------------|-------|
| **Bulk Lookups** | `$ids \| ForEach-Object { Get-MgUser -UserId $_ }` | `$ids \| Invoke-MgxRequest '/users/{id}'` |
| **Bulk Updates** | `$ids \| ForEach-Object { Update-MgUser ... }` | `$urls \| Invoke-MgxBatchRequest -Method PATCH -Body @{...}` |
| **Exporting Data** | `$all = Get-MgUser -All; $all \| Export-Csv ...` | `Export-MgxCollection /users -OutputFile users.jsonl` |
| **Fault Protection** | None (manual script handling required) | Built-in or via `Enable-MgxResilience` |
| **Observability** | No retry/throttle metrics | `Get-MgxTelemetry` |
| **Dead Connection Handling** | Indefinite hangs | Body-read timeouts + 2-min connection recycling |
| **Beta Endpoints** | Requires `Microsoft.Graph.Beta` module | `-ApiVersion beta` flag |

---

## Cmdlets

| Cmdlet | Description |
|--------|-------------|
| `Invoke-MgxRequest` | Executes Graph requests with automatic concurrency, retries, and rate limiting |
| `Invoke-MgxBatchRequest` | Batches up to 20 requests into a single HTTP POST call |
| `Export-MgxCollection` | Streams paginated API results directly to JSONL with checkpointing |
| `Expand-MgxRelation` | Performs concurrent fan-out lookups to expand related object attributes |
| `Sync-MgxDelta` | Manages stateful delta queries and state token storage |
| `Set-MgxOption` / `Get-MgxOption` | Configures global limits, retry counts, timeouts, and circuit breaker thresholds |
| `Enable-MgxResilience` / `Disable-MgxResilience` | Injects/removes Mgx resilience policies into native Microsoft.Graph SDK cmdlets |
| `Get-MgxTelemetry` | Outputs execution stats (HTTP duration, rate limit delays, retry counts, resource units) |

---

## Resilience Architecture

All HTTP operations pass through four layered [Polly 8.x](https://github.com/App-vNext/Polly) resilience strategies:

1. **Rate Limiter:** Token bucket policy (200 burst capacity / 50 requests/sec refill) to stay within Graph limits.
2. **Retry Policy:** Up to 7 retries with exponential backoff and jitter for transient errors (`429`, `500`, `502`, `503`, `504`). Honors `Retry-After` headers.
3. **Circuit Breaker:** Breaks execution circuit when error rates exceed 10% (half-open probe after 15s).
4. **Timeout:** Enforces a 30s per-request limit and a 300s cumulative ceiling across retries.

*Batch writes add an adaptive pacing engine using AIMD (Additive Increase / Multiplicative Decrease) to adjust payload throughput dynamically.*

---

## Requirements & Building

### Requirements
- **PowerShell:** 7.5+
- **Auth Module:** [Microsoft.Graph.Authentication](https://www.powershellgallery.com/packages/Microsoft.Graph.Authentication) 2.10.0+

### Build from Source

```powershell
./build.ps1
```

Dependencies (`Microsoft.Graph.Core`, `Polly.Core`, `System.Threading.RateLimiting`) are loaded into an isolated **Assembly Load Context** to eliminate version conflicts with existing PowerShell modules.

---

## License

[MIT](LICENSE)