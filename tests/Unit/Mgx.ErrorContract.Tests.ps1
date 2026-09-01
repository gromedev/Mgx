#Requires -Modules Pester

<#
    Pins what about_Mgx_Errors documents, as far as it reaches without a transport:
    pre-flight failures terminate with stable ids, land in -ErrorVariable, and set $?
    false. Per-item wire behavior is pinned by the xUnit suite, which fakes HTTP -
    ErrorActionMatrixTests drives all four preferences across the six failure contexts.
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

# Carried as text rather than as scriptblocks so each case can be driven twice: once inside a
# try, which is what proves it terminates, and once bare with -ErrorVariable appended, which is
# the only way to read $? and the collected record off the statement that terminated.
$script:PreFlightCases = @(
    @{ Name = 'an absolute Uri on Invoke-MgxRequest'
       Command = "Invoke-MgxRequest -Uri 'https://graph.microsoft.com/v1.0/users'"
       ErrorId = 'AbsoluteUriNotAllowed' }
    @{ Name = 'an absolute Uri on Export-MgxCollection'
       Command = "Export-MgxCollection -Uri 'https://graph.microsoft.com/v1.0/users' -OutputFile ([IO.Path]::GetTempFileName())"
       ErrorId = 'AbsoluteUriNotAllowed' }
    @{ Name = 'an absolute Uri on Sync-MgxDelta'
       Command = "Sync-MgxDelta -Uri 'https://graph.microsoft.com/v1.0/users/delta' -DeltaPath ([IO.Path]::GetTempFileName())"
       ErrorId = 'AbsoluteUriNotAllowed' }
    @{ Name = '-Search without ConsistencyLevel'
       Command = "Invoke-MgxRequest -Uri '/users' -Search 'displayName:x'"
       ErrorId = 'ConsistencyLevelRequired' }
    @{ Name = 'a -Uri without {id} on Expand-MgxRelation'
       Command = "[pscustomobject]@{ id = 'x' } | Expand-MgxRelation -Uri '/users/x/manager' -As manager"
       ErrorId = 'MissingIdPlaceholder' }
)

Describe 'Pre-flight failures terminate' {
    It '<Name> terminates with <ErrorId>' -TestCases $script:PreFlightCases {
        # A terminating error is catchable; a non-terminating one is not. Catching it IS
        # the assertion that it terminates.
        $caught = $null
        try { & ([scriptblock]::Create($Command)) } catch { $caught = $_ }
        $caught | Should -Not -BeNullOrEmpty
        $caught.FullyQualifiedErrorId | Should -Match "^$ErrorId"
    }

    It '<Name> sets $? false and lands in -ErrorVariable' -TestCases $script:PreFlightCases {
        # ThrowTerminatingError ends the statement, not the script, so $? and -ErrorVariable are
        # readable from the statement after it - which a catch would have hidden. Both reads have
        # to happen in the session that ran the command, so the probe runs in a fresh runspace:
        # under Pester the same error takes the whole test body with it, and $? outside the probe
        # would report the wrapper rather than the cmdlet.
        $probe = [powershell]::Create()
        try {
            $script = @'
Import-Module -Name '__MANIFEST__' -Force
$ev = $null
__COMMAND__ -ErrorVariable ev 2>$null
[pscustomobject]@{ Ok = $?; Collected = @($ev) }
'@
            $null = $probe.AddScript(
                $script.Replace('__MANIFEST__', $script:Paths.ManifestPath).Replace('__COMMAND__', $Command))
            $result = $probe.Invoke()[0]
        }
        finally { $probe.Dispose() }

        $result.Ok | Should -BeFalse
        $result.Collected.Count | Should -Be 1

        # A terminating error reaches -ErrorVariable as the CmdletInvocationException carrying the
        # record, not as the record itself.
        $collected = $result.Collected[0]
        $record = if ($collected -is [System.Management.Automation.ErrorRecord]) { $collected }
                  else { $collected.ErrorRecord }
        $record.FullyQualifiedErrorId | Should -Match "^$ErrorId"
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
