# Changelog

## 1.0.4

Fixes ported from the [Microsoft365DSC fork](https://github.com/Microsoft365DSC/mgx), contributed by Fabien Tschanz.

- Fixed Mgx cmdlets keeping the credentials of the first `Connect-MgGraph` call in a session. The cached HTTP client was keyed on tenant id alone, so reconnecting to the same tenant with a different application, certificate, account, or scope set silently reused the previous identity and its permissions. The client is now keyed on a fingerprint of the full auth context, and a rotated client secret is caught as well
- Fixed a JSON string passed to `-Body` being silently dropped. `-Body (@{...} | ConvertTo-Json)` arrived wrapped in a `PSObject`, missed the string branch of the serializer, and went out as `{}` — an empty write that Graph accepted without error. `IDictionary`, `PSCustomObject`, and array bodies now all serialize, including nested
- Fixed `Enable-MgxResilience` staying bound to the pre-reconnect SDK client; resilience is now re-injected automatically when the Graph identity changes
- Fixed `Set-MgxOption -TotalTimeoutSeconds` not reaching the HTTP client, which kept the timeout it was first built with (`HttpClient.Timeout` is immutable after the first request; the client is now rebuilt when the value changes)
- Fixed a single 429 slowing `Invoke-MgxBatchRequest` for the rest of the session. The write pacing rate now climbs back after clean chunks and fully restores after five minutes without throttling
- Fixed the internal type cache never invalidating when `Microsoft.Graph.Authentication` was re-imported at a different version or into a fresh load context, which left Mgx resolving a stale `GraphSession`
- Fixed JSON integers above 2^53 losing precision (were widened to `double`)
- Added `-Debug` request/response tracing on every cmdlet — single requests, pagination, fan-out, and `$batch` — with credential redaction and 4 KB body truncation
- A batch item whose body is not valid JSON now fails on its own instead of aborting the whole batch; `-Body` on a GET request now warns instead of being silently ignored
- The `SdkVersion` header is now derived from the assembly version, set once in `Directory.Build.props`, instead of a hand-maintained constant that had drifted
- Internal: the xUnit and Pester test suites and a build-and-test CI workflow now live in the repository

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