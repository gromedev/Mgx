#Requires -Modules Pester

<#
    Pins what about_Mgx_Errors documents, as far as it reaches without a transport:
    pre-flight failures terminate with stable ids, land in -ErrorVariable, and set $?
    false. Per-item wire behavior (BatchItemError, -ErrorAction Stop mid-batch) is pinned
    by the xUnit suite, which fakes HTTP.
#>

BeforeAll {
    Import-Module -Name (Join-Path (Split-Path -Parent $PSScriptRoot) 'TestHarness.psm1') -Force
    $script:Paths = Get-MgxTestPath
    Import-Module -Name $script:Paths.ManifestPath -Force -ErrorAction Stop
}

Describe 'about_Mgx_Errors' {
    It 'ships and documents both halves of the contract' {
        $topic = Get-Help about_Mgx_Errors -ErrorAction Stop | Out-String
        $topic | Should -Match 'TERMINATING ERRORS'
        $topic | Should -Match 'NON-TERMINATING ERRORS'
        $topic | Should -Match 'CANCELLATION'
    }
}

Describe 'Pre-flight failures terminate' {
    It '<Name> terminates with <ErrorId>' -TestCases @(
        @{ Name = 'an absolute Uri on Invoke-MgxRequest'
           Script = { Invoke-MgxRequest -Uri 'https://graph.microsoft.com/v1.0/users' }
           ErrorId = 'AbsoluteUriNotAllowed' }
        @{ Name = 'an absolute Uri on Export-MgxCollection'
           Script = { Export-MgxCollection -Uri 'https://graph.microsoft.com/v1.0/users' -OutputFile ([IO.Path]::GetTempFileName()) }
           ErrorId = 'AbsoluteUriNotAllowed' }
        @{ Name = 'an absolute Uri on Sync-MgxDelta'
           Script = { Sync-MgxDelta -Uri 'https://graph.microsoft.com/v1.0/users/delta' -DeltaPath ([IO.Path]::GetTempFileName()) }
           ErrorId = 'AbsoluteUriNotAllowed' }
        @{ Name = '-Search without ConsistencyLevel'
           Script = { Invoke-MgxRequest -Uri '/users' -Search 'displayName:x' }
           ErrorId = 'ConsistencyLevelRequired' }
        @{ Name = 'a -Uri without {id} on Expand-MgxRelation'
           Script = { [pscustomobject]@{ id = 'x' } | Expand-MgxRelation -Uri '/users/x/manager' -As manager }
           ErrorId = 'MissingIdPlaceholder' }
    ) {
        # A terminating error is catchable; a non-terminating one is not. Catching it IS
        # the assertion that it terminates.
        $caught = $null
        try { & $Script } catch { $caught = $_ }
        $caught | Should -Not -BeNullOrEmpty
        $caught.FullyQualifiedErrorId | Should -Match "^$ErrorId"
    }
}

Describe 'about_Mgx_Errors vocabulary' {
    It 'names every category the presentation map can produce' {
        # A text check only: the categories themselves are asserted where records are
        # actually produced (the xUnit wire suites). This holds the DOCUMENT to the
        # vocabulary, not the code to the document.
        $topic = Get-Help about_Mgx_Errors | Out-String
        foreach ($category in 'ObjectNotFound', 'PermissionDenied', 'AuthenticationError',
                              'LimitsExceeded', 'ResourceUnavailable', 'ConnectionError',
                              'InvalidArgument', 'ResourceExists') {
            $topic | Should -Match $category
        }
    }
}
