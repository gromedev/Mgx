# Live tests

Every exported cmdlet, run against a real tenant, the way a caller runs it.

The rest of the suite mocks HTTP. That proves the code does what it was written to do; it
cannot prove the request leaving the process is one Graph accepts. `Enable-MgxResilience`
shipped a wrapper client with no `BaseAddress` — every relative-URI SDK call threw before
reaching the wire — against a fully green suite, because every mocked test passed an absolute
URI. These tests exist for that class of defect.

They are read-only. Nothing here writes to a tenant.

## Running them

```bash
export AZURE_TENANT_ID='...'
export AZURE_CLIENT_ID='...'
export AZURE_CLIENT_CERTIFICATE_PATH="$HOME/.certs/....pfx"
pwsh -c 'Invoke-Pester -Path ./tests/Live'
```

Missing credentials is a **failure**, not a skip. A live suite that skips itself when
unconfigured is one that silently stops running.

The directory cmdlets want a tenant with enough objects to page — a few hundred users is
plenty. `Get-MgxContent` needs `Files.Read.All`/`Sites.Read.All`, which a directory-only app
registration will not have, so those two tests are skipped unless you point them at a drive
item on a tenant that does:

```bash
export MGX_LIVE_CONTENT_URI='/drives/<driveId>/items/<itemId>/content'
```

## Keeping them honest

`tests/Unit/Mgx.LiveCoverage.Tests.ps1` fails if an exported cmdlet has no `Describe` block
here. It parses this directory rather than running it, so it works in CI with no credentials.
Adding a cmdlet without a live test breaks the build.

If a cmdlet genuinely cannot be exercised live, still add the `Describe` with a `-Skip` and a
reason. The point is that the decision is recorded rather than absent — which is how seven of
twelve cmdlets came to have no live coverage at all without anyone noticing.

## What they will not catch

They cover the happy path of each cmdlet. Failure modes, resume, throttling and accuracy are
covered by `tests/Mgx.IntegrationTests` (mocked, deterministic) and `tests/benchmarks` (live,
measured). A green run here means "the cmdlets work against Graph", not "the module is correct".
