# Build script for Mgx PowerShell module
# Compiles both projects and stages output into module/ directory

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ModuleRoot = Join-Path $PSScriptRoot 'module'
$DepsDir = Join-Path $ModuleRoot 'Dependencies'

Write-Host "Building Mgx ($Configuration)..." -ForegroundColor Cyan

# Version gate: MgxSdkVersion derives the SdkVersion header from the assembly version set in
# Directory.Build.props, so the header can no longer drift from the code on its own (it said
# 0.3.0 for three releases while the module shipped 1.0.x). The manifest ModuleVersion is
# still a separate declaration, so the one remaining mismatch - props vs manifest - is
# checked before the compile, failing in a second instead of a minute.
$manifestVersion = (Import-PowerShellDataFile (Join-Path $ModuleRoot 'mgx.psd1')).ModuleVersion
$propsFile = Join-Path $PSScriptRoot 'Directory.Build.props'
$propsVersion = ([xml](Get-Content $propsFile -Raw)).Project.PropertyGroup.Version
if (-not $propsVersion) {
    throw "Version gate failed: no <Version> found in $propsFile"
}
if ($propsVersion -ne $manifestVersion) {
    throw "Version gate failed: Directory.Build.props <Version> is '$propsVersion' but mgx.psd1 " +
          "ModuleVersion is '$manifestVersion'. Update one to match the other."
}
Write-Host "Version gate: mgx/$propsVersion matches manifest" -ForegroundColor DarkGray

# Clean previous build artifacts
if (Test-Path $DepsDir) { Remove-Item $DepsDir -Recurse -Force }
$binDir = Join-Path $ModuleRoot 'bin'
if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
Get-ChildItem $ModuleRoot -Filter '*.dll' | Remove-Item -Force
Get-ChildItem $ModuleRoot -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $ModuleRoot -Filter '*.deps.json' | Remove-Item -Force

# Build the solution
dotnet build "$PSScriptRoot/Mgx.slnx" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# Copy Cmdlets + Engine DLLs (load in default ALC)
# Detect TFM from csproj instead of hardcoding (survives TFM upgrades)
$csproj = [xml](Get-Content "$PSScriptRoot/src/Mgx.Cmdlets/Mgx.Cmdlets.csproj")
$tfm = $csproj.Project.PropertyGroup.TargetFramework
$CmdletsOutput = Join-Path $PSScriptRoot "src/Mgx.Cmdlets/bin/$Configuration/$tfm"
Copy-Item "$CmdletsOutput/Mgx.Cmdlets.dll" $ModuleRoot -Force
Copy-Item "$CmdletsOutput/Mgx.Cmdlets.pdb" $ModuleRoot -Force -ErrorAction SilentlyContinue
Copy-Item "$CmdletsOutput/Mgx.Engine.dll" $ModuleRoot -Force
Copy-Item "$CmdletsOutput/Mgx.Engine.pdb" $ModuleRoot -Force -ErrorAction SilentlyContinue

# Copy deps.json (useful for diagnostic tooling; deleted by clean step above)
$depsJson = Join-Path $CmdletsOutput 'Mgx.Cmdlets.deps.json'
if (Test-Path $depsJson) {
    Copy-Item $depsJson $ModuleRoot -Force
} else {
    Write-Warning "Mgx.Cmdlets.deps.json not found in build output - diagnostics may be limited"
}

# Copy third-party dependencies into Dependencies/ (loaded via ALC Resolving handler on first use)
# Polly.Core and System.Threading.RateLimiting are not in the module root;
# Mgx.Engine is NOT here - it is loaded via RequiredAssemblies in mgx.psd1 (see comment there).
New-Item $DepsDir -ItemType Directory -Force | Out-Null

# Copy dependency DLLs that need ALC isolation (Polly, RateLimiting)
# After D1+D2: replaced Microsoft.Extensions.Http.Resilience with direct Polly.Core 8.6.6
# This eliminated 28 transitive dependencies (51 total down to 23 packages)
$DepsToIsolate = @(
    'Polly.Core.dll'
    'System.Threading.RateLimiting.dll'
)

foreach ($dep in $DepsToIsolate) {
    $depPath = Join-Path $CmdletsOutput $dep
    if (Test-Path $depPath) {
        Copy-Item $depPath $DepsDir -Force
    } else {
        throw "Required dependency not found: $dep (expected at $depPath)"
    }
}

# Verify module output is in expected state
$RequiredRoot = @('Mgx.Cmdlets.dll', 'Mgx.Engine.dll', 'mgx.psd1', 'mgx.psm1')
$RequiredDeps = @('Polly.Core.dll', 'System.Threading.RateLimiting.dll')

foreach ($f in $RequiredRoot) {
    if (-not (Test-Path (Join-Path $ModuleRoot $f))) {
        throw "Module integrity check failed: $f missing from module root"
    }
}
foreach ($f in $RequiredDeps) {
    if (-not (Test-Path (Join-Path $DepsDir $f))) {
        throw "Module integrity check failed: $f missing from Dependencies/"
    }
}
$orphans = Get-ChildItem $DepsDir -Filter 'Mgx.*.dll'
if ($orphans) {
    throw "Module integrity check failed: Mgx assemblies found in Dependencies/ (should only be in root): $($orphans.Name -join ', ')"
}

# Write build hash for staleness detection
$hashInputFiles = @(
    (Join-Path $ModuleRoot 'Mgx.Cmdlets.dll'),
    (Join-Path $ModuleRoot 'Mgx.Engine.dll')
) | Where-Object { Test-Path $_ }
$combinedHash = ($hashInputFiles | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash }) -join '|'
$buildStamp = @{
    Hash      = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($combinedHash))) -Algorithm SHA256).Hash
    Timestamp = (Get-Date -Format 'o')
    Configuration = $Configuration
}
$buildStamp | ConvertTo-Json | Set-Content (Join-Path $ModuleRoot '.build-hash') -Force

Write-Host "`nBuild complete!" -ForegroundColor Green
Write-Host "Module output: $ModuleRoot" -ForegroundColor Yellow
Write-Host "`nTo use:" -ForegroundColor Cyan
Write-Host "  Import-Module '$ModuleRoot/mgx.psd1'" -ForegroundColor White
Write-Host "  Connect-MgGraph -Scopes 'User.Read.All'" -ForegroundColor White
Write-Host "  Invoke-MgxRequest /users -All" -ForegroundColor White
