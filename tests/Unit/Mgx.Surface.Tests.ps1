#Requires -Modules Pester

$script:GraphDataCmdlets = @(
    'Invoke-MgxRequest'
    'Invoke-MgxBatchRequest'
    'Expand-MgxRelation'
    'Sync-MgxDelta'
)

$script:ExpectedCmdlets = @(
    'Invoke-MgxRequest'
    'Invoke-MgxBatchRequest'
    'Export-MgxCollection'
    'Expand-MgxRelation'
    'Set-MgxOption'
    'Get-MgxOption'
    'Enable-MgxResilience'
    'Disable-MgxResilience'
    'Get-MgxResilience'
    'Get-MgxTelemetry'
    'Sync-MgxDelta'
    'Get-MgxContent'
)

BeforeAll {
    Import-Module -Name (Join-Path (Split-Path -Parent $PSScriptRoot) 'TestHarness.psm1') -Force
    $script:Paths = Get-MgxTestPath

    Import-Module -Name $script:Paths.ManifestPath -Force -ErrorAction Stop

    # Discovery and run use separate scopes, so the lists above are not visible
    # inside It blocks. Re-bind them here for the run phase.
    $script:ExpectedCmdletNames = @(
        'Invoke-MgxRequest'
        'Invoke-MgxBatchRequest'
        'Export-MgxCollection'
        'Expand-MgxRelation'
        'Set-MgxOption'
        'Get-MgxOption'
        'Enable-MgxResilience'
        'Disable-MgxResilience'
        'Get-MgxResilience'
        'Get-MgxTelemetry'
        'Sync-MgxDelta'
        'Get-MgxContent'
    )
}

Describe 'Module import' {
    It 'imports without error' {
        Get-Module -Name $script:Paths.ModuleName | Should -Not -BeNullOrEmpty
    }

    It 'exports exactly the cmdlets listed in the manifest' {
        $exported = (Get-Module -Name $script:Paths.ModuleName).ExportedCmdlets.Keys

        $exported | Should -HaveCount $script:ExpectedCmdletNames.Count
        foreach ($cmdlet in $script:ExpectedCmdletNames)
        {
            $exported | Should -Contain $cmdlet
        }
    }

    It 'loads the format file without error' {
        # A malformed ps1xml surfaces here rather than at first output
        { Update-FormatData -PrependPath $script:Paths.FormatPath -ErrorAction Stop } |
            Should -Not -Throw
    }
}

Describe 'Output contract' {
    It '<_> declares Hashtable output' -ForEach $script:GraphDataCmdlets {
        $outputTypes = (Get-Command -Name $_).OutputType.Type.FullName

        $outputTypes | Should -Contain 'System.Collections.Hashtable'
        # 2.0 is a hard cutover: no cmdlet may still advertise the 1.x PSObject shape
        $outputTypes | Should -Not -Contain 'System.Management.Automation.PSObject'
    }

    It 'Invoke-MgxRequest also declares String output for -Raw' {
        (Get-Command -Name Invoke-MgxRequest).OutputType.Type.FullName |
            Should -Contain 'System.String'
    }

    It 'informational cmdlets keep their strongly typed output' {
        (Get-Command -Name Get-MgxOption).OutputType.Type.FullName |
            Should -Contain 'Mgx.Cmdlets.Models.MgxOptionOutput'
        (Get-Command -Name Get-MgxTelemetry).OutputType.Type.FullName |
            Should -Contain 'Mgx.Cmdlets.Models.MgxTelemetryOutput'
    }
}

Describe 'Pipeline input contract' {
    It 'Invoke-MgxRequest keeps the Id alias on the fan-out parameter' {
        (Get-Command -Name Invoke-MgxRequest).Parameters['InputObject'].Aliases |
            Should -Contain 'Id'
    }

    It 'Invoke-MgxBatchRequest accepts an array of URLs or request descriptions' {
        $parameter = (Get-Command -Name Invoke-MgxBatchRequest).Parameters['Uri']

        $parameter.Aliases | Should -Contain 'Url'
    }
}

Describe 'Cmdlet safety attributes' {
    It 'Invoke-MgxRequest supports ShouldProcess for write operations' {
        (Get-Command -Name Invoke-MgxRequest).Parameters.Keys | Should -Contain 'WhatIf'
    }

    It 'Invoke-MgxBatchRequest supports ShouldProcess' {
        (Get-Command -Name Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'WhatIf'
    }

    It 'restricts <Cmdlet> -<Parameter> to the documented values' -ForEach @(
        @{ Cmdlet = 'Invoke-MgxRequest';      Parameter = 'ApiVersion'; Valid = @('v1.0', 'beta') }
        @{ Cmdlet = 'Invoke-MgxRequest';      Parameter = 'Method';     Valid = @('GET', 'POST', 'PATCH', 'PUT', 'DELETE') }
        @{ Cmdlet = 'Invoke-MgxBatchRequest'; Parameter = 'Method';     Valid = @('GET', 'POST', 'PATCH', 'PUT', 'DELETE') }
    ) {
        $validateSet = (Get-Command -Name $Cmdlet).Parameters[$Parameter].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } |
            Select-Object -First 1

        $validateSet | Should -Not -BeNullOrEmpty
        foreach ($value in $Valid)
        {
            $validateSet.ValidValues | Should -Contain $value
        }
    }
}

Describe 'Adaptive pacing surface' {
    It 'Set-MgxOption exposes -NoAdaptivePacing' {
        (Get-Command -Name Set-MgxOption).Parameters.Keys | Should -Contain 'NoAdaptivePacing'
    }

    It 'Get-MgxOption reports pacing enabled by default' {
        (Get-MgxOption).NoAdaptivePacing | Should -BeFalse
    }

    It 'Get-MgxTelemetry reports the pacing counters and state' {
        $telemetry = Get-MgxTelemetry
        $names = $telemetry.PSObject.Properties.Name
        $names | Should -Contain 'AdaptivePacingWaitMs'
        $names | Should -Contain 'AdaptivePacingActivations'
        $names | Should -Contain 'LastThrottlePercentage'
        $names | Should -Contain 'PacingState'
    }
}

Describe 'Help' {
    It 'ships help content for <_>' -ForEach $script:ExpectedCmdlets {
        $help = Get-Help -Name $_ -ErrorAction SilentlyContinue

        $help | Should -Not -BeNullOrEmpty
        $help.Synopsis | Should -Not -BeNullOrEmpty
        $help.Synopsis | Should -Not -Match '^\s*$'
    }
}

Describe 'Module removal' {
    # Regression test for the OnRemove ordering: AlcInitializer must run static cleanup
    # before detaching the ALC resolver (and now also detach the assembly-load hook), or
    # Remove-Module throws FileNotFoundException for Polly.Core and the module can never
    # be unloaded. Fixed in v1.0.3; kept working in v1.0.4.
    #
    # Only the no-throw contract is asserted here: Pester's BeforeAll import leaves a
    # module-table entry visible from this scope even after a successful removal, so
    # Get-Module is not a reliable signal inside Pester. A plain pwsh session unloads
    # cleanly (covered by the release smoke test).
    It 'Remove-Module succeeds' {
        { Remove-Module -Name $script:Paths.ModuleName -Force -ErrorAction Stop } |
            Should -Not -Throw
    }
}

Describe 'Help content is authored, not auto-generated' {
    # The pre-existing check only asserted a non-empty Synopsis. PowerShell SYNTHESISES a Synopsis
    # from the syntax block when no MAML exists, so that assertion passed with the help file
    # deleted outright. These read the compiled MAML directly instead of going through Get-Help,
    # which is both deterministic and immune to updatable-help lookups.
    BeforeAll {
        # The 'Module removal' Describe above unloads mgx, and Pester runs Describes in file
        # order - so Get-Command here found nothing, every parameter list came back empty, and
        # the comparison below passed having checked nothing. Re-import to be order-independent.
        Import-Module -Name $script:Paths.ManifestPath -Force -ErrorAction Stop

        $script:MamlPath = Join-Path $script:Paths.ModuleRoot 'en-US/Mgx.Cmdlets.dll-Help.xml'
        $script:HelpMd   = Join-Path (Split-Path -Parent $script:Paths.ModuleRoot) 'module/help'
        $script:Maml     = if (Test-Path $script:MamlPath) { [xml](Get-Content $script:MamlPath -Raw) } else { $null }
    }

    It 'ships a compiled MAML file' {
        $script:MamlPath | Should -Exist
        $script:Maml | Should -Not -BeNullOrEmpty
    }

    It 'compiled help carries every markdown example for <_>' -ForEach $script:ExpectedCmdlets {
        $md = Join-Path $script:HelpMd "$_.md"
        if (-not (Test-Path $md)) { Set-ItResult -Skipped -Because "no markdown help for $_"; return }

        # Capture the name FIRST: inside Where-Object, $_ and $PSItem rebind to the pipeline
        # item, so comparing against $PSItem there compares each command to itself.
        $name = $PSItem
        $inMarkdown = @(Select-String -Path $md -Pattern '^### Example' -AllMatches).Count
        $cmd = @($script:Maml.helpItems.command | Where-Object { $_.details.name.Trim() -eq $name })[0]
        $cmd | Should -Not -BeNullOrEmpty -Because "$name has no entry in the compiled MAML"

        $inMaml = @($cmd.examples.example).Count
        $inMaml | Should -Be $inMarkdown -Because "module/help/$name.md documents $inMarkdown example(s) but the MAML carries $inMaml - regenerate the compiled help"
    }

    It 'compiled help documents every parameter of <_>' -ForEach $script:ExpectedCmdlets {
        $common = [System.Management.Automation.PSCmdlet]::CommonParameters +
                  [System.Management.Automation.PSCmdlet]::OptionalCommonParameters
        $name = $PSItem
        # -ErrorAction Stop, and assert non-empty: without both, a Get-Command that cannot resolve
        # the cmdlet leaves $real empty, $missing empty, and the assertion passes having checked
        # nothing at all - the exact vacuous-green this whole block exists to prevent.
        $cmdInfo = Get-Command -Name $name -ErrorAction Stop
        $cmdInfo | Should -Not -BeNullOrEmpty -Because "$name must be resolvable for this comparison to mean anything"
        # Several cmdlets (Get-MgxOption, the resilience trio) legitimately take no parameters
        # beyond the common ones, so an empty list here is a real result, not a failure.
        $real = @($cmdInfo.Parameters.Keys | Where-Object { $_ -notin $common })

        $cmd = @($script:Maml.helpItems.command | Where-Object { $_.details.name.Trim() -eq $name })[0]
        $cmd | Should -Not -BeNullOrEmpty -Because "$name has no entry in the compiled MAML"
        $documented = @($cmd.parameters.parameter.name)

        $missing = @($real | Where-Object { $_ -notin $documented })
        $missing | Should -BeNullOrEmpty -Because "$name has parameter(s) absent from compiled help: $($missing -join ', ')"
    }
}
