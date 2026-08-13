# Changelog

## 2.0.0

Breaking release. The Graph-data cmdlets change output shape. The platform floor moves *down* to PowerShell 7.4 (LTS). The breaking work was contributed in the [Microsoft365DSC fork](https://github.com/Microsoft365DSC/mgx) by Fabien Tschanz.

### Breaking

- Invoke-MgxRequest, Invoke-MgxBatchRequest, Expand-MgxRelation and Sync-MgxDelta now emit case-insensitive Hashtables instead of PSObjects, matching the shape Invoke-MgGraphRequest returns so results drop into scripts written for the Graph SDK without adaptation. Code that needs the previous shape can use `-Raw | ConvertFrom-Json`.
- @odata.type is returned verbatim under its own key rather than renamed to ODataType, which keeps the type annotation Graph expects on read-modify-write round-trips. All other @odata.* transport metadata is stripped, including @odata.etag: the etag changes on every write, so preserving it would make two reads of an unchanged entity compare unequal and surface as phantom drift in state-comparison consumers. Callers that need the If-Match tag can read it from the raw payload (-Raw | ConvertFrom-Json).
- The six Graph entity format views (Mgx.User, Mgx.Group, Mgx.Application, Mgx.ServicePrincipal, Mgx.DirectoryRole, Mgx.BatchResult) were removed. PowerShell always renders a dictionary with its built-in Name/Value view, so a custom view can never be selected for the new output. Key enumeration order is undefined; use Select-Object or Format-Table to pin column order.
- The platform floor moves **down**, not up: the module now targets .NET 8 and supports PowerShell 7.4 (LTS) and later, where 1.x required 7.5. Nothing in mgx ever needed the newer runtime — the `net9.0` target dated to 1.0.1 and was never driven by the code. The one .NET 9 API in the tree (`HttpContent.LoadIntoBufferAsync(CancellationToken)`, in the `-Debug` tracer) is replaced with a `WaitAsync` equivalent that keeps the same timeout behaviour.
- Microsoft.Graph.Authentication is no longer declared in RequiredModules. Auth was already resolved reflectively at call time, so the declaration only forced the SDK onto every consumer — including hosts that supply their own Graph auth and want nothing but the resilience layer — and installed a second copy alongside whatever they already loaded. The module now imports without it; cmdlets that need a token report GraphAuthModuleNotLoaded with install instructions instead of pointing at Connect-MgGraph, which would name a cmdlet the session does not have.

### Fixed

- Action endpoints that answer a write with a collection envelope, such as /directoryObjects/getByIds, emitted the envelope as a single object with a value property instead of one object per element. Envelope unwrapping is now applied on every response path, including fan-out bulk writes, with the same truncation warning when the response carries @odata.nextLink.
- Piping an object to a fan-out request (`-Uri '/users/{id}'`) bound it to a string parameter, putting the literal type name into the request URL with no error. Fan-out now reads the id member from Hashtables and PSCustomObjects, and reports a MissingPipelineId error when input carries neither.
- Invoke-MgxBatchRequest could not consume its own output, because per-item input parsing only read PSObject properties. It now reads members from Hashtables and PSCustomObjects alike, so failed items can be piped straight back in for retry.

### Changed

- Polly.Core 8.7.0 and System.Threading.RateLimiting 10.0.10.

## 1.0.5

### Fixed

- Export-MgxCollection no longer loses progress on interrupted first runs. Cancelled runs promote temp files to the output path with matching checkpoints, and killed or crashed runs recover on the next invocation by trimming orphaned temp files to the last checkpointed item count. The "Resume with:" hint now accurately reflects first-run states.
- Get-MgxTelemetry now includes per-item Retry-After wait times from $batch processing into RetryDelayMs instead of incorrectly reporting zero retry delay for throttled batch sessions.
- Count-discrepancy warnings now also trigger when an enumeration returns more items than @odata.count reported (using a 0.5% threshold with a 50-item floor) and recommend deduplicating on id downstream.

### Documentation

- Added write-cost pacing documentation to about_Mgx_Tuning, explaining that Graph throttles write operations rather than batch items (e.g., a group create with 20 member bindings costs ~21 writes) and how to budget BatchItemsPerSecond accordingly.
- Updated Export-MgxCollection help with details on mid-page checkpointing, first-run recovery, and downstream id deduplication hygiene.
- Adjusted performance claims for Invoke-MgxRequest, updating the batch-vs-fan-out speedup from 3–4x down to the measured ~1.5x for PATCH operations at 1k scale.
- Rebuilt the README based on measured results from the new benchmark suite.


## 1.0.4

### Fixed

- Fixed Mgx cmdlets caching credentials from only the first Connect-MgGraph call. HTTP clients are now keyed on a fingerprint of the full authentication context so reconnecting with a different application, certificate, account, scope set, or secret properly updates the identity.
- Fixed JSON string input to -Body being silently converted to an empty object ({}) when passed as a PSObject. IDictionary, PSCustomObject, array, and nested bodies now serialize correctly.
- Fixed Enable-MgxResilience remaining bound to pre-reconnect SDK clients by automatically re-injecting resilience settings when the Graph identity changes.
- Fixed Set-MgxOption -TotalTimeoutSeconds failing to update the HTTP client timeout for existing sessions by rebuilding the client on value changes.
- Fixed temporary 429 throttling permanently slowing Invoke-MgxBatchRequest rates for the remainder of the session; write pacing now recovers after clean chunks and fully resets after five unthrottled minutes.
- Fixed the internal type cache failing to invalidate when Microsoft.Graph.Authentication was re-imported at a different version or in a new load context.
- Fixed JSON integers larger than 2^53 losing precision due to double-precision floating-point conversion.

### Changed

- Batch items with invalid JSON bodies now fail individually instead of aborting the entire batch.
- Passing -Body on a GET request now emits a warning instead of being silently ignored.
- SdkVersion header strings are now derived automatically from the assembly version set in Directory.Build.props.

### Added

- Added -Debug request and response tracing across all cmdlets (single requests, pagination, fan-out, $batch) with credential redaction and a 4 KB body truncation limit.
- Added xUnit and Pester test suites alongside a build-and-test CI workflow to the repository.


## 1.0.3

- Fixed `Remove-Module Mgx` failing and leaving the module loaded. Static-state cleanup moved into `AlcInitializer.OnRemove`, ahead of the ALC resolver detaching; it previously ran from the module `OnRemove` scriptblock, by which point `Polly.Core` was unresolvable. Only triggered when no Graph request had run in the session
- Fixed the `SdkVersion` request header reporting `mgx/0.3.0` regardless of the installed version. It now matches the module version, and `build.ps1` fails the build if the constant and the manifest ever disagree
- Extracted cmdlet lifecycle and JSON conversion to `MgxCmdletCore`. Internal refactor; no change to the cmdlet surface
- Treat `CA1416` as an error in both projects, so a Windows-only API reaching the cross-platform code paths fails the build instead of throwing `PlatformNotSupportedException` on Linux or macOS

## 1.0.2

- Fixed Linux install: renamed `Mgx.psd1`, `Mgx.psm1`, and `Mgx.Format.ps1xml` to lowercase so `Install-Module Mgx` works on case-sensitive filesystems (PSGallery lowercases the module folder name)
- Updated `about_Mgx_Tuning` version reference to v1.0.1

## 1.0.1

- Added tab completion for the Uri parameter on all cmdlets that accept Graph API paths (Invoke-MgxRequest, Invoke-MgxBatchRequest, Export-MgxCollection, Expand-MgxRelation, Sync-MgxDelta)
- Extracted `CircuitBreakerMessage` protected property on `MgxCmdletBase` to eliminate repeated inline circuit breaker message strings across six cmdlet files
- Removed redundant XML doc comments on self-documenting members in `MgxCmdletBase` and `ResilientGraphClient`