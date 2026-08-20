# Releasing

`main` carries one commit per release. There are no merge commits: a release is `dev` squashed
onto `main`, tagged, then published.

## Before

1. **Both suites, both platforms.**
   ```
   dotnet test tests/Mgx.IntegrationTests/Mgx.IntegrationTests.csproj
   pwsh -c 'Invoke-Pester -Path ./tests/Unit'
   ```
   Run them on Windows too. A green run on one OS says nothing about file locking, path
   comparison or the exceptions Windows raises where Unix raises different ones - each of those
   has shipped a defect here.

2. **The live suite, actually run.**
   ```
   pwsh -c 'Invoke-Pester -Path ./tests/Live'
   ```
   With credentials, against a real tenant. The mocked suites cannot see a request Graph
   rejects: `Enable-MgxResilience` once shipped a client with no `BaseAddress`, breaking every
   relative-URI call, with every test green. "Skipped" is not a pass - see `tests/Live/README.md`.

3. **Install it the way a user does.** Build Release, then in a *fresh* shell with only the
   Gallery dependencies present, import the staged `module/`, run `Test-ModuleManifest`, and
   check `Get-Help` for every exported cmdlet. Stale generated help drops parameters silently.

4. **Read the published numbers.** Every figure in `README.md` must be reproducible from
   `tests/benchmarks` as it stands. Re-measure rather than reuse: the tenant's throttling
   ceiling has moved by 40% between consecutive days.

5. **Version and notes.** `ModuleVersion` in `module/mgx.psd1`, the manifest's `ReleaseNotes`,
   and the `CHANGELOG.md` section all agree.

## Squashing

```bash
git checkout main && git pull origin main
git merge --squash dev
git commit -F <release message>

git diff --stat dev        # MUST be empty - if it prints anything, the squash lost something

git tag -a vX.Y.Z -m "mgx X.Y.Z"
git push origin main && git push origin vX.Y.Z
```

The squash message is the public record of the release; the CHANGELOG carries the per-defect
detail. Describe what the release *is*, not the churn that produced it.

## After

`Publish-Module` from the tagged `main`.

`dev` and `main` share no history once squashed, so `dev` reads as permanently ahead. Retire it
and branch a fresh one from `main` for the next version rather than carrying the divergence.
