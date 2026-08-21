# Live tests

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

The tests size themselves from the tenant, so a 19-user directory and a 100,000-user one both
work. **Prefer a tenant with a SharePoint licence**: `Get-MgxContent` is the only cmdlet that
needs one, and without it those two tests skip, so a tenant that has it is the only place the
suite covers everything. Point it at any drive item:

```bash
export MGX_LIVE_CONTENT_URI='/drives/<driveId>/items/<itemId>/content'
```

A tenant with no SharePoint answers every sites/drives call with `BadRequest: Tenant does not
have a SPO license`, and no permission grant changes that — the content roles can be present and
still inert.

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
