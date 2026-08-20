<#
    Asserts that every exported cmdlet is exercised against a real tenant by tests/Live.

    This runs in CI, where there are no credentials, because it reads the live tests rather than
    running them. That is the point: the live suite proves the cmdlets work, and this proves the
    live suite is complete. Without it, coverage decays silently - a cmdlet gets added, nobody
    writes a live test, and the gap is invisible until a user finds it.

    It exists because that is exactly what happened: Enable-MgxResilience shipped a wrapper with
    no BaseAddress, breaking every relative-URI SDK call, while seven of twelve cmdlets had never
    been run against a tenant at all and nothing anywhere said so.
#>

BeforeAll {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    Import-Module "$repo/module/mgx.psd1" -Force
    $script:Exported = (Get-Command -Module mgx -CommandType Cmdlet,Function).Name | Sort-Object

    $script:LiveDir = "$repo/tests/Live"
    $script:LiveText = if (Test-Path $script:LiveDir) {
        (Get-ChildItem $script:LiveDir -Filter '*.Tests.ps1' | Get-Content -Raw) -join "`n"
    } else { '' }

    # A Describe block naming the cmdlet is the contract. Parsed, not regexed loosely, so a
    # mention inside a comment or a string cannot satisfy it.
    $script:Described = @()
    if ($script:LiveText) {
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($script:LiveText, [ref]$null, [ref]$null)
        $script:Described = $ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.CommandAst] -and
            $n.GetCommandName() -eq 'Describe'
        }, $true) | ForEach-Object {
            $arg = $_.CommandElements | Select-Object -Skip 1 -First 1
            if ($arg -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $arg.Value }
        }
    }
}

Describe 'Live test coverage' {
    It 'has a live test directory' {
        Test-Path $script:LiveDir | Should -BeTrue -Because 'tests/Live is where cmdlets are proven against a real tenant'
    }

    It 'covers every exported cmdlet' {
        $missing = $script:Exported | Where-Object { $_ -notin $script:Described }
        $missing -join ', ' | Should -BeNullOrEmpty -Because @'
every exported cmdlet needs a Describe block in tests/Live naming it. A mocked test proves the
code does what it was written to do; only a live one proves the request Graph receives is one it
accepts. If a cmdlet genuinely cannot be tested live, add the Describe with an explaining -Skip
so the decision is recorded rather than absent.
'@
    }

    It 'describes nothing that is not exported' {
        $stale = $script:Described | Where-Object { $_ -notin $script:Exported }
        $stale -join ', ' | Should -BeNullOrEmpty -Because 'a live test for a cmdlet that no longer exists is dead weight'
    }
}
