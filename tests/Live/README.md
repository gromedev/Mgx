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

An app with a secret rather than a certificate works too - set `AZURE_CLIENT_SECRET`
instead of `AZURE_CLIENT_CERTIFICATE_PATH`. If both are set, the certificate wins.
Neither leaves anything on disk.

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

## The throttle test

`Mgx.Throttle.Tests.ps1` covers the one thing mocks cannot: what `Enable-MgxResilience` does
when Graph is actually refusing requests. The SDK's own retry handler sits inside the wrap and
answers `429` itself, so a throttled session looks identical to a clean one — the pacer stays in
slow-start and telemetry reports zero retries while the SDK sleeps out of sight inside the call.
With that handler left armed, a caller's request eventually fails outright.

It needs a second machine. One client cannot exhaust a tenant's budget through a single NAT'd
connection — 587 req/s from a laptop against 4,641 from a VM on the same network, same test,
same minute — and below roughly 700 req/s the tenant never refuses anything, so the run proves
nothing at all.

```bash
export MGX_LIVE_THROTTLE_SSH='user@host'          # a host with real egress
export MGX_LIVE_THROTTLE_KEY="$HOME/.ssh/id_..."  # optional identity file
pwsh -c 'Invoke-Pester -Path ./tests/Live/Mgx.Throttle.Tests.ps1'
```

The certificate never leaves this machine: the assertion is signed locally and only the
resulting bearer token crosses, on stdin, never as an argument and never to a file.

Unlike the rest of this directory it is **not read-only in effect** — it deliberately exhausts
the tenant's request budget and leaves it refusing requests for several minutes afterwards. Run
it last, or alone. It skips when `MGX_LIVE_THROTTLE_SSH` is unset, because most people running
this suite have no second machine to hand; when it is set, failing to reach a throttle is a
failure rather than a skip, so a green run cannot mean "there was nothing to see".

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
