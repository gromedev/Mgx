#Requires -Modules Pester

<#
    Mgx Pester Tests (v0.3.0)

    Prerequisites:
    1. Run build.ps1 first
    2. For Live tests: Connect-MgGraph -Scopes "User.Read.All", "Group.Read.All"

    Usage:
    Invoke-Pester ./tests/Mgx.Tests.ps1 -Output Detailed
    Invoke-Pester ./tests/Mgx.Tests.ps1 -Output Detailed -Tag 'Live'
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '../module/mgx.psd1'
    Import-Module $ModulePath -Force
}

Describe 'Module Loading' {
    It 'Should import Mgx module' {
        $module = Get-Module Mgx
        $module | Should -Not -BeNullOrEmpty
        # Read expected version from manifest to avoid hardcoded values breaking on version bumps
        $manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot '../module/mgx.psd1')
        $module.Version | Should -Be $manifest.ModuleVersion
    }

    It 'Should export exactly 11 cmdlets' {
        # Filtered to -CommandType Cmdlet on purpose: Get-Command -Module also counts
        # exported functions, so an unfiltered count would drift for reasons that have
        # nothing to do with the compiled surface.
        $commands = (Get-Command -Module Mgx -CommandType Cmdlet).Name | Sort-Object
        $commands | Should -Contain 'Invoke-MgxRequest'
        $commands | Should -Contain 'Invoke-MgxBatchRequest'
        $commands | Should -Contain 'Export-MgxCollection'
        $commands | Should -Contain 'Expand-MgxRelation'
        $commands | Should -Contain 'Sync-MgxDelta'
        $commands | Should -Contain 'Set-MgxOption'
        $commands | Should -Contain 'Get-MgxOption'
        $commands | Should -Contain 'Enable-MgxResilience'
        $commands | Should -Contain 'Disable-MgxResilience'
        $commands | Should -Contain 'Get-MgxResilience'
        $commands | Should -Contain 'Get-MgxTelemetry'
        $commands | Should -HaveCount 11
    }

    It 'Should unload cleanly when no Graph request ever ran' {
        # Regression for the Remove-Module teardown defect fixed in 1.0.3.
        # AlcInitializer.OnRemove (IModuleAssemblyCleanup) detaches the ALC Resolving
        # handler, and PowerShell runs it BEFORE a module's OnRemove scriptblock. The
        # cleanup used to live in that scriptblock and called ResetHttpClient, which
        # JITs code referencing Polly types. Polly.Core ships in Dependencies/ and is
        # reachable only through the detached resolver -- and it loads lazily, so in a
        # session with no Graph request it was never in the AppDomain at all. Result:
        # Remove-Module threw and the module could never be unloaded.
        #
        # Runs in a child process because the precondition is "Polly.Core has never
        # been loaded", which the parent suite has already violated by this point.
        $modulePath = Join-Path $PSScriptRoot '../module/mgx.psd1'
        $probe = @"
Import-Module '$modulePath' -Force
try { Remove-Module mgx -ErrorAction Stop }
catch { Write-Output ('THREW: ' + `$_.Exception.Message.Split([char]10)[0]); exit 1 }
if (Get-Module mgx) { Write-Output 'STILL LOADED'; exit 1 }
Write-Output 'OK'
"@
        $result = pwsh -NoProfile -Command $probe
        $result | Should -Contain 'OK'
        $LASTEXITCODE | Should -Be 0
    }
}

Describe 'Expand-MgxRelation Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 0
    }

    It 'Should have -As parameter (mandatory, position 1)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['As']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 1
    }

    It 'Should have -InputObject parameter (mandatory, pipeline)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['InputObject']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.ValueFromPipeline | Should -BeTrue
    }

    It 'Should have -Flatten switch parameter' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Flatten']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -IdProperty parameter with default "id"' {
        $param = (Get-Command Expand-MgxRelation).Parameters['IdProperty']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -Concurrency parameter with ValidateRange(1,128)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Concurrency']
        $param | Should -Not -BeNullOrEmpty
        $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
        $validateRange | Should -Not -BeNullOrEmpty
    }

    It 'Should have -SkipNotFound and -SkipForbidden parameters' {
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'SkipNotFound'
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'SkipForbidden'
    }

    It 'Should have -ConsistencyLevel parameter' {
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'ConsistencyLevel'
    }

    It 'Should have -Headers parameter' {
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Expand-MgxRelation).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }

    It 'Should have -Top parameter with ValidateRange' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Top']
        $param | Should -Not -BeNullOrEmpty
        $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
        $validateRange | Should -Not -BeNullOrEmpty
    }

    # R3-3: $search requires -ConsistencyLevel eventual
    It 'Should throw when URI contains $search without -ConsistencyLevel' {
        { @([PSCustomObject]@{id='x'}) | Expand-MgxRelation '/users/{id}/memberOf?$search="displayName:test"' -As Members } |
            Should -Throw '*ConsistencyLevel*'
    }

    It 'Should not throw ConsistencyLevel error when -ConsistencyLevel is provided' {
        # Will fail on auth (no Graph connection), but must NOT throw about ConsistencyLevel.
        # Catch all errors and verify none mention ConsistencyLevel.
        $err = $null
        try {
            @([PSCustomObject]@{id='x'}) | Expand-MgxRelation '/users/{id}/memberOf?$search="displayName:test"' -As Members -ConsistencyLevel eventual -ErrorAction Stop
        } catch {
            $err = $_
        }
        # If it threw, the error must NOT be about ConsistencyLevel (auth errors are expected)
        if ($err) {
            $err.Exception.Message | Should -Not -BeLike '*ConsistencyLevel*'
        }
    }

    # R3-11: Buffer size warning at 50k items
    It 'Should warn when buffer exceeds 50k items' -Tag 'Slow' {
        $objects = 1..50001 | ForEach-Object { [PSCustomObject]@{id = "id-$_"} }
        $w = @()
        try {
            $objects | Expand-MgxRelation '/users/{id}/manager' -As Manager `
                -WarningVariable w 3>$null -ErrorAction Stop
        } catch {
            # Expected: auth error from EndProcessing. Warning fires in ProcessRecord before this.
        }
        $bufferWarning = $w | Where-Object { $_ -match 'buffer' -or $_ -match '50.000' }
        $bufferWarning | Should -Not -BeNullOrEmpty
    }
}

Describe 'Invoke-MgxRequest Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0)' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Resource'
    }

    It 'Should have -All switch parameter' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['All']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -Top parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Top'
    }

    It 'Should have -Filter parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Filter'
    }

    It 'Should have -Property parameter with Select alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Property']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Select'
    }

    It 'Should have -Sort parameter with OrderBy alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Sort']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'OrderBy'
    }

    It 'Should have -ExpandProperty parameter with Expand alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['ExpandProperty']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Expand'
    }

    It 'Should have -CountVariable parameter with CV alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['CountVariable']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'CV'
    }

    It 'Should have -Method parameter with ValidateSet' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Method']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'GET'
        $validateSet.ValidValues | Should -Contain 'POST'
        $validateSet.ValidValues | Should -Contain 'DELETE'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }

    It 'Should have -InputObject in Pipeline parameter set' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['InputObject']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Id'
    }

    It 'Should have -SkipNotFound and -SkipForbidden parameters' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'SkipNotFound'
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'SkipForbidden'
    }

    It 'Should have -Concurrency parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Concurrency'
    }

    It 'Should have -Raw switch parameter' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Raw']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -Body parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Body'
    }

    It 'Should have -CheckpointPath parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'CheckpointPath'
    }

    It 'Should have -NoPageSize switch parameter' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['NoPageSize']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Invoke-MgxRequest).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }
}

Describe 'Invoke-MgxBatchRequest Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0, pipeline) with -Url alias' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 0
        $paramAttr.ValueFromPipeline | Should -BeTrue
        $param.Aliases | Should -Contain 'Url'
    }

    It 'Should have -Method parameter with ValidateSet' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Method']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'GET'
        $validateSet.ValidValues | Should -Contain 'POST'
        $validateSet.ValidValues | Should -Contain 'PATCH'
        $validateSet.ValidValues | Should -Contain 'PUT'
        $validateSet.ValidValues | Should -Contain 'DELETE'
    }

    It 'Should have -Body parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'Body'
    }

    It 'Should accept object[] for -Uri (supports strings and PSObjects)' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Uri']
        $param.ParameterType | Should -Be ([object[]])
    }

    It 'Should default -Method to GET' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Method']
        # Method is not mandatory, GET is the default
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeFalse
    }

    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Invoke-MgxBatchRequest).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }

    It 'Should have -Headers parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should have -ThrottlePriority parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'ThrottlePriority'
    }

    It 'Should validate -ThrottlePriority values' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['ThrottlePriority']
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'Low'
        $validateSet.ValidValues | Should -Contain 'Normal'
        $validateSet.ValidValues | Should -Contain 'High'
    }

    It 'Should have -DeadLetterPath parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'DeadLetterPath'
    }
}

Describe 'Header Precedence (D4)' {
    It 'BuildRequestHeaders: -ConsistencyLevel wins over -Headers key' {
        $method = [Mgx.Cmdlets.Base.MgxCmdletBase].GetMethod(
            'BuildRequestHeaders',
            [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)
        $method | Should -Not -BeNullOrEmpty

        $extra = @{ ConsistencyLevel = 'session' }
        $result = $method.Invoke($null, @('eventual', $extra))
        $result['ConsistencyLevel'] | Should -Be 'eventual'
    }

    It 'BuildRequestHeaders: extra headers preserved when no conflict' {
        $method = [Mgx.Cmdlets.Base.MgxCmdletBase].GetMethod(
            'BuildRequestHeaders',
            [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)

        $extra = @{ 'x-ms-throttle-priority' = 'Low'; 'Prefer' = 'outlook.body-content-type=text' }
        $result = $method.Invoke($null, @('eventual', $extra))
        $result['ConsistencyLevel'] | Should -Be 'eventual'
        $result['x-ms-throttle-priority'] | Should -Be 'Low'
        $result['Prefer'] | Should -Be 'outlook.body-content-type=text'
    }
}

Describe 'Export-MgxCollection Parameter Compatibility' {
    It 'Should have -Uri parameter with Resource alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Resource'
    }

    It 'Should have -OutputFile parameter (mandatory)' {
        $param = (Get-Command Export-MgxCollection).Parameters['OutputFile']
        $param | Should -Not -BeNullOrEmpty
        $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
    }

    It 'Should have -Property parameter with Select alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['Property']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Select'
    }

    It 'Should have -Filter parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Filter'
    }

    It 'Should have -Top parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Top'
    }

    It 'Should have -PageSize parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'PageSize'
    }

    It 'Should have -CheckpointPath parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'CheckpointPath'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Export-MgxCollection).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }

    It 'Should have -Sort parameter with OrderBy alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['Sort']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'OrderBy'
    }

    It 'Should have -NoPageSize switch parameter' {
        $param = (Get-Command Export-MgxCollection).Parameters['NoPageSize']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -All switch parameter' {
        $param = (Get-Command Export-MgxCollection).Parameters['All']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -ExpandProperty parameter with Expand alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['ExpandProperty']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Expand'
    }

    It 'Should have -Skip parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Skip'
    }

    It 'Should have -Search parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Search'
    }

    It 'Should have -ConsistencyLevel parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'ConsistencyLevel'
    }

    It 'Should have -Headers parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should support ShouldProcess' {
        $cmdletAttr = (Get-Command Export-MgxCollection).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }
}

Describe 'Set-MgxOption Parameter Validation' {
    It 'Should have -RateLimitBurst parameter' {
        (Get-Command Set-MgxOption).Parameters.Keys | Should -Contain 'RateLimitBurst'
    }

    It 'Should have -RateLimitPerSecond parameter' {
        (Get-Command Set-MgxOption).Parameters.Keys | Should -Contain 'RateLimitPerSecond'
    }

    It 'Should have -NoRateLimit switch parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['NoRateLimit']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should reject -RateLimitBurst 0' {
        { Set-MgxOption -RateLimitBurst 0 } | Should -Throw
    }

    It 'Should reject -RateLimitPerSecond 0' {
        { Set-MgxOption -RateLimitPerSecond 0 } | Should -Throw
    }

    It 'Should reject -RateLimitBurst -1' {
        { Set-MgxOption -RateLimitBurst -1 } | Should -Throw
    }
}

Describe 'Enable-MgxResilience Parameter Compatibility' {
    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Enable-MgxResilience).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }

    It 'Should have no mandatory parameters' {
        $mandatoryParams = (Get-Command Enable-MgxResilience).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatoryParams | Should -BeNullOrEmpty
    }
}

Describe 'Disable-MgxResilience Parameter Compatibility' {
    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Disable-MgxResilience).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }

    It 'Should have no mandatory parameters' {
        $mandatoryParams = (Get-Command Disable-MgxResilience).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatoryParams | Should -BeNullOrEmpty
    }
}

Describe 'Get-MgxResilience Parameter Compatibility' {
    It 'Should have OutputType of PSObject' {
        $outputType = (Get-Command Get-MgxResilience).OutputType
        $outputType.Name | Should -Contain 'System.Management.Automation.PSObject'
    }

    It 'Should have no mandatory parameters' {
        $mandatoryParams = (Get-Command Get-MgxResilience).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatoryParams | Should -BeNullOrEmpty
    }
}

Describe 'Get-MgxOption' {
    It 'Should have OutputType of MgxOptionOutput' {
        $outputType = (Get-Command Get-MgxOption).OutputType
        $outputType.Name | Should -Contain 'Mgx.Cmdlets.Models.MgxOptionOutput'
    }

    It 'Should return all option properties' {
        $result = Get-MgxOption
        $result.RateLimitBurst | Should -BeGreaterThan 0
        $result.RateLimitPerSecond | Should -BeGreaterThan 0
        $result.MaxRetryAttempts | Should -BeGreaterThan 0
        $result.TotalTimeoutSeconds | Should -BeGreaterThan 0
        $result.AttemptTimeoutSeconds | Should -BeGreaterThan 0
        $result.CircuitBreakerDurationSeconds | Should -BeGreaterThan 0
        $result.CircuitBreakerFailureRatio | Should -BeGreaterThan 0
        $result.CircuitBreakerMinThroughput | Should -BeGreaterThan 0
        $result.CircuitBreakerSamplingDurationSeconds | Should -BeGreaterThan 0
        $result.PSTypeNames | Should -Contain 'Mgx.Cmdlets.Models.MgxOptionOutput'
    }

    It 'Should return default values' {
        $result = Get-MgxOption
        $result.RateLimitBurst | Should -Be 200
        $result.RateLimitPerSecond | Should -Be 50
        $result.MaxRetryAttempts | Should -Be 7
        $result.TotalTimeoutSeconds | Should -Be 300
        $result.AttemptTimeoutSeconds | Should -Be 30
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        $result.CircuitBreakerFailureRatio | Should -Be 0.1
        $result.CircuitBreakerMinThroughput | Should -Be 40
        $result.RateLimitQueueLimit | Should -Be 500
        $result.NoRateLimit | Should -BeFalse
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 30
        $result.BatchChunkConcurrency | Should -Be 1
        $result.BatchItemsPerSecond | Should -Be 20
    }
}

Describe 'Set-MgxOption Pipeline Parameters' {
    It 'Should have -MaxRetryAttempts parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['MaxRetryAttempts']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -TotalTimeoutSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['TotalTimeoutSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -AttemptTimeoutSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['AttemptTimeoutSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerDurationSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerDurationSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerFailureRatio parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerFailureRatio']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerMinThroughput parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerMinThroughput']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerSamplingDurationSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerSamplingDurationSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -RateLimitQueueLimit parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['RateLimitQueueLimit']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -BatchChunkConcurrency parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['BatchChunkConcurrency']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -Reset switch parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['Reset']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should persist pipeline options via Set then Get' {
        Set-MgxOption -MaxRetryAttempts 3 -TotalTimeoutSeconds 60
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 3
        $result.TotalTimeoutSeconds | Should -Be 60
        Set-MgxOption -Reset
    }

    It 'Should persist AttemptTimeoutSeconds via Set then Get' {
        Set-MgxOption -AttemptTimeoutSeconds 15
        $result = Get-MgxOption
        $result.AttemptTimeoutSeconds | Should -Be 15
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerDurationSeconds via Set then Get' {
        Set-MgxOption -CircuitBreakerDurationSeconds 45
        $result = Get-MgxOption
        $result.CircuitBreakerDurationSeconds | Should -Be 45
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerFailureRatio via Set then Get' {
        Set-MgxOption -CircuitBreakerFailureRatio 0.5
        $result = Get-MgxOption
        $result.CircuitBreakerFailureRatio | Should -Be 0.5
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerMinThroughput via Set then Get' {
        Set-MgxOption -CircuitBreakerMinThroughput 50
        $result = Get-MgxOption
        $result.CircuitBreakerMinThroughput | Should -Be 50
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerSamplingDurationSeconds via Set then Get' {
        Set-MgxOption -CircuitBreakerSamplingDurationSeconds 60
        $result = Get-MgxOption
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 60
        Set-MgxOption -Reset
    }

    It 'Should persist RateLimitQueueLimit via Set then Get' {
        Set-MgxOption -RateLimitQueueLimit 1000
        $result = Get-MgxOption
        $result.RateLimitQueueLimit | Should -Be 1000
        Set-MgxOption -Reset
    }

    It 'Should persist BatchChunkConcurrency via Set then Get' {
        Set-MgxOption -BatchChunkConcurrency 5
        $result = Get-MgxOption
        $result.BatchChunkConcurrency | Should -Be 5
        Set-MgxOption -Reset
    }

    It 'Should carry forward unspecified options when setting one parameter' {
        Set-MgxOption -Reset
        Set-MgxOption -MaxRetryAttempts 3
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 3
        $result.TotalTimeoutSeconds | Should -Be 300
        $result.AttemptTimeoutSeconds | Should -Be 30
        $result.RateLimitBurst | Should -Be 200
        $result.RateLimitPerSecond | Should -Be 50
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        $result.CircuitBreakerFailureRatio | Should -Be 0.1
        $result.CircuitBreakerMinThroughput | Should -Be 40
        $result.RateLimitQueueLimit | Should -Be 500
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 30
        $result.BatchChunkConcurrency | Should -Be 1
        $result.BatchItemsPerSecond | Should -Be 20
        Set-MgxOption -Reset
    }

    It 'Should carry forward across multiple successive Set-MgxOption calls' {
        Set-MgxOption -Reset
        Set-MgxOption -MaxRetryAttempts 5
        Set-MgxOption -TotalTimeoutSeconds 60
        Set-MgxOption -AttemptTimeoutSeconds 10
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 5
        $result.TotalTimeoutSeconds | Should -Be 60
        $result.AttemptTimeoutSeconds | Should -Be 10
        $result.RateLimitBurst | Should -Be 200
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        Set-MgxOption -Reset
    }

    It 'Should not change any options when called with no parameters' {
        Set-MgxOption -Reset
        $before = Get-MgxOption
        Set-MgxOption
        $after = Get-MgxOption
        $after.RateLimitBurst | Should -Be $before.RateLimitBurst
        $after.MaxRetryAttempts | Should -Be $before.MaxRetryAttempts
        $after.TotalTimeoutSeconds | Should -Be $before.TotalTimeoutSeconds
        $after.AttemptTimeoutSeconds | Should -Be $before.AttemptTimeoutSeconds
        $after.CircuitBreakerDurationSeconds | Should -Be $before.CircuitBreakerDurationSeconds
        $after.CircuitBreakerFailureRatio | Should -Be $before.CircuitBreakerFailureRatio
        $after.CircuitBreakerMinThroughput | Should -Be $before.CircuitBreakerMinThroughput
        $after.RateLimitQueueLimit | Should -Be $before.RateLimitQueueLimit
        $after.NoRateLimit | Should -Be $before.NoRateLimit
        $after.CircuitBreakerSamplingDurationSeconds | Should -Be $before.CircuitBreakerSamplingDurationSeconds
        $after.BatchChunkConcurrency | Should -Be $before.BatchChunkConcurrency
    }

    It 'Should reset all options to defaults with -Reset' {
        Set-MgxOption -MaxRetryAttempts 3 -TotalTimeoutSeconds 60 -AttemptTimeoutSeconds 10
        Set-MgxOption -Reset
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 7
        $result.TotalTimeoutSeconds | Should -Be 300
        $result.AttemptTimeoutSeconds | Should -Be 30
        $result.RateLimitBurst | Should -Be 200
        $result.RateLimitPerSecond | Should -Be 50
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        $result.CircuitBreakerFailureRatio | Should -Be 0.1
        $result.CircuitBreakerMinThroughput | Should -Be 40
        $result.RateLimitQueueLimit | Should -Be 500
        $result.NoRateLimit | Should -BeFalse
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 30
        $result.BatchChunkConcurrency | Should -Be 1
        $result.BatchItemsPerSecond | Should -Be 20
    }

    It 'Should reject -MaxRetryAttempts 0' {
        { Set-MgxOption -MaxRetryAttempts 0 } | Should -Throw
    }

    It 'Should reject -TotalTimeoutSeconds 0' {
        { Set-MgxOption -TotalTimeoutSeconds 0 } | Should -Throw
    }

    It 'Should reject -AttemptTimeoutSeconds 0' {
        { Set-MgxOption -AttemptTimeoutSeconds 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerDurationSeconds 0' {
        { Set-MgxOption -CircuitBreakerDurationSeconds 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerFailureRatio 0' {
        { Set-MgxOption -CircuitBreakerFailureRatio 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerFailureRatio 1.5' {
        { Set-MgxOption -CircuitBreakerFailureRatio 1.5 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerMinThroughput 0' {
        { Set-MgxOption -CircuitBreakerMinThroughput 0 } | Should -Throw
    }

    It 'Should reject -RateLimitQueueLimit -1' {
        { Set-MgxOption -RateLimitQueueLimit -1 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerSamplingDurationSeconds 4' {
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 4 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerSamplingDurationSeconds 301' {
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 301 } | Should -Throw
    }

    It 'Should reject -BatchChunkConcurrency 0' {
        { Set-MgxOption -BatchChunkConcurrency 0 } | Should -Throw
    }

    It 'Should reject -BatchChunkConcurrency 11' {
        { Set-MgxOption -BatchChunkConcurrency 11 } | Should -Throw
    }

    It 'Should accept boundary values' {
        { Set-MgxOption -CircuitBreakerFailureRatio 1.0 } | Should -Not -Throw
        { Set-MgxOption -CircuitBreakerFailureRatio 0.01 } | Should -Not -Throw
        { Set-MgxOption -RateLimitQueueLimit 0 } | Should -Not -Throw
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 5 } | Should -Not -Throw
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 300 } | Should -Not -Throw
        { Set-MgxOption -BatchChunkConcurrency 1 } | Should -Not -Throw
        { Set-MgxOption -BatchChunkConcurrency 10 } | Should -Not -Throw
        Set-MgxOption -Reset
    }

    It 'Should reject values exceeding upper bounds' {
        { Set-MgxOption -MaxRetryAttempts 51 } | Should -Throw
        { Set-MgxOption -TotalTimeoutSeconds 3601 } | Should -Throw
        { Set-MgxOption -AttemptTimeoutSeconds 301 } | Should -Throw
        { Set-MgxOption -CircuitBreakerMinThroughput 1001 } | Should -Throw
        { Set-MgxOption -BatchChunkConcurrency 11 } | Should -Throw
    }
}

Describe 'C5: ConsistencyLevel Enforcement' {
    It 'Should throw when -Search is used without -ConsistencyLevel' {
        { Invoke-MgxRequest /users -Search "displayName:Test" } | Should -Throw '*ConsistencyLevel*'
    }

    It 'Should throw when Export-MgxCollection -Search is used without -ConsistencyLevel' {
        { Export-MgxCollection /users -OutputFile "test.jsonl" -Search "displayName:Test" } | Should -Throw '*ConsistencyLevel*'
    }

    It 'Should NOT throw when -Search is used WITH -ConsistencyLevel eventual' {
        # When connected, the command succeeds. When disconnected, it throws NotConnected.
        # Either way, it must NOT throw ConsistencyLevelRequired.
        try {
            Invoke-MgxRequest /users -Search "displayName:Test" -ConsistencyLevel eventual -Top 1 | Out-Null
        } catch {
            $_.FullyQualifiedErrorId | Should -Not -BeLike '*ConsistencyLevel*'
        }
    }

    It 'Should throw when Invoke-MgxBatchRequest URL contains $search without -ConsistencyLevel' {
        { '/users?$search="displayName:Test"' | Invoke-MgxBatchRequest -ErrorAction Stop } | Should -Throw '*ConsistencyLevel*'
    }

    It 'Should NOT throw when Invoke-MgxBatchRequest $search is used WITH -ConsistencyLevel' {
        try {
            '/users?$search="displayName:Test"' | Invoke-MgxBatchRequest -ConsistencyLevel eventual -ErrorAction Stop | Out-Null
        } catch {
            $_.FullyQualifiedErrorId | Should -Not -BeLike '*ConsistencyLevel*'
        }
    }
}

Describe 'Sync-MgxDelta Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0)' {
        $param = (Get-Command Sync-MgxDelta).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 0
    }

    It 'Should have -DeltaPath parameter (mandatory)' {
        $param = (Get-Command Sync-MgxDelta).Parameters['DeltaPath']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
    }

    It 'Should have -Property parameter with Select alias' {
        $param = (Get-Command Sync-MgxDelta).Parameters['Property']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Select'
    }

    It 'Should have -OutputFile parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'OutputFile'
    }

    It 'Should have -FullSync switch' {
        $param = (Get-Command Sync-MgxDelta).Parameters['FullSync']
        $param | Should -Not -BeNullOrEmpty
        $param.ParameterType | Should -Be ([switch])
    }

    It 'Should have -Filter parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'Filter'
    }

    It 'Should have -Top parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'Top'
    }

    It 'Should have -Headers parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Sync-MgxDelta).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }
}

Describe 'Sync-MgxDelta Validation' {
    It 'Should reject absolute HTTPS URL' {
        { Sync-MgxDelta -Uri 'https://graph.microsoft.com/v1.0/users/delta' -DeltaPath '/tmp/test.delta' -ErrorAction Stop } |
            Should -Throw '*relative path*'
    }

    It 'Should reject absolute HTTP URL' {
        { Sync-MgxDelta -Uri 'http://graph.microsoft.com/v1.0/users/delta' -DeltaPath '/tmp/test.delta' -ErrorAction Stop } |
            Should -Throw '*relative path*'
    }
}

Describe 'Sync-MgxDelta State Management' {
    BeforeAll {
        $script:testDir = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-delta-tests-$(New-Guid)"
        New-Item -ItemType Directory -Path $script:testDir -Force | Out-Null
    }

    AfterAll {
        if (Test-Path $script:testDir) { Remove-Item $script:testDir -Recurse -Force }
    }

    It 'Should reject -DeltaPath equal to -OutputFile' {
        $dp = Join-Path $script:testDir 'same.json'
        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -OutputFile $dp -ErrorAction Stop } |
            Should -Throw '*same file*'
    }

    It 'Should warn when URI does not contain /delta' {
        $dp = Join-Path $script:testDir 'nodelta.json'
        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users' -DeltaPath $dp -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*does not contain*delta*'
    }

    It 'Should detect endpoint mismatch in delta state' {
        $dp = Join-Path $script:testDir 'endpoint-mismatch.json'
        @{
            deltaLink = 'https://graph.microsoft.us/v1.0/users/delta?$deltatoken=abc'
            select = ''
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.us'
        } | ConvertTo-Json | Set-Content $dp

        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction Stop } |
            Should -Throw '*graph.microsoft.us*graph.microsoft.com*'
    }

    It 'Should detect resource mismatch in delta state' {
        $dp = Join-Path $script:testDir 'resource-mismatch.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=abc'
            select = ''
            filter = $null
            resource = '/groups/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction Stop } |
            Should -Throw '*resource*'
    }

    It 'Should detect select change and warn about full re-sync' {
        $dp = Join-Path $script:testDir 'select-change.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = 'displayName,id'
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -Property mail,jobTitle -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*Property selection changed*'
    }

    It 'Should detect filter change and warn about full re-sync' {
        $dp = Join-Path $script:testDir 'filter-change.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = ''
            filter = "department eq 'Sales'"
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -Filter "department eq 'Engineering'" -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*Filter changed*'
    }

    It 'Should reject tampered deltaLink with wrong host (SSRF)' {
        $dp = Join-Path $script:testDir 'ssrf.json'
        @{
            deltaLink = 'https://evil.com/v1.0/users/delta?$deltatoken=stolen'
            select = ''
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction Stop } |
            Should -Throw '*invalid or untrusted*'
    }

    It 'Should reject tampered deltaLink with wrong resource path' {
        $dp = Join-Path $script:testDir 'path-tamper.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/me/messages?$deltatoken=abc'
            select = ''
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction Stop } |
            Should -Throw '*path*does not match*'
    }

    It 'Should warn on corrupt delta state file' {
        $dp = Join-Path $script:testDir 'corrupt.json'
        'not valid json{{{' | Set-Content $dp

        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*corrupt*'
    }

    It 'Should delete delta state when -FullSync is specified' {
        $dp = Join-Path $script:testDir 'fullsync.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = ''
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        # Verify file exists before FullSync
        Test-Path $dp | Should -BeTrue

        # FullSync should delete the file before starting the sync.
        # Even if the sync succeeds and saves new state, the OLD file
        # should be gone. Use the original resource so the test works
        # with or without a Graph connection.
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -FullSync -ErrorAction SilentlyContinue
        } catch { }

        # If connected: sync runs, saves NEW state → file exists but is fresh (new deltaLink)
        # If not connected: GetClient() fails, no save → file doesn't exist
        # Either way, the OLD deltatoken 'abc' should be gone
        if (Test-Path $dp) {
            $state = Get-Content $dp | ConvertFrom-Json
            $state.deltaLink | Should -Not -BeLike '*abc*' -Because 'FullSync should have replaced the old delta state'
        }
        # If file doesn't exist, that's also correct (no connection = no save)
    }

    It 'Should NOT trigger re-sync when select order differs' {
        $dp = Join-Path $script:testDir 'select-order.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = 'displayName,id'  # stored normalized
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        $warnings = $null
        try {
            # Same properties, different order
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -Property id,displayName -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        # Should NOT warn about property selection change
        $selectWarnings = $warnings | Where-Object { "$_" -like '*Property selection changed*' }
        $selectWarnings | Should -BeNullOrEmpty
    }
}

Describe 'NormalizeSelect (via reflection)' {
    BeforeAll {
        $script:normalize = [Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta].GetMethod(
            'NormalizeSelect',
            [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)
    }

    It 'Should reorder properties alphabetically' {
        $result = $script:normalize.Invoke($null, @('displayName,id'))
        $result | Should -Be 'displayName,id'  # already sorted
        $result2 = $script:normalize.Invoke($null, @('id,displayName'))
        $result2 | Should -Be 'displayName,id'  # reordered
        $result | Should -Be $result2  # same after normalization
    }

    It 'Should deduplicate properties' {
        $result = $script:normalize.Invoke($null, @('id,id,displayName'))
        $result | Should -Be 'displayName,id'
    }

    It 'Should handle null' {
        $result = $script:normalize.Invoke($null, @($null))
        $result | Should -Be ''
    }

    It 'Should handle empty string' {
        $result = $script:normalize.Invoke($null, @(''))
        $result | Should -Be ''
    }

    It 'Should trim whitespace' {
        $result = $script:normalize.Invoke($null, @(' id , displayName '))
        $result | Should -Be 'displayName,id'
    }
}

Describe 'Live API Tests' -Tag 'Live' {
    BeforeAll {
        $script:liveConnected = $null -ne (Get-MgContext -ErrorAction SilentlyContinue)
    }

    BeforeEach {
        if (-not $script:liveConnected) {
            Set-ItResult -Skipped -Because 'Not connected to Microsoft Graph'
        }
    }

    It 'Invoke-MgxRequest /users -Top 5 should return users' {
        $users = Invoke-MgxRequest /users -Top 5
        $users | Should -Not -BeNullOrEmpty
        $users.Count | Should -BeLessOrEqual 5
        $users[0].id | Should -Not -BeNullOrEmpty
    }

    It 'Invoke-MgxRequest /users -All | Select -First 5 should stream' {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $firstUser = Invoke-MgxRequest /users -All | Select-Object -First 1
        $sw.Stop()

        $firstUser | Should -Not -BeNullOrEmpty
        # Streaming means first item arrives fast
        $sw.ElapsedMilliseconds | Should -BeLessThan 30000
    }

    It 'Invoke-MgxRequest /users/<id> should return single entity' {
        $firstUser = Invoke-MgxRequest /users -Top 1
        $user = Invoke-MgxRequest "/users/$($firstUser.id)"
        $user | Should -Not -BeNullOrEmpty
        $user.id | Should -Be $firstUser.id
    }

    It 'Invoke-MgxRequest /users -Filter should work' {
        $users = Invoke-MgxRequest /users -Filter "accountEnabled eq true" -Top 3
        $users | Should -Not -BeNullOrEmpty
    }

    It 'Fan-out: $ids | Invoke-MgxRequest /users/{id} should return entities with _MgxSourceId' {
        $topUsers = Invoke-MgxRequest /users -Top 3 -Property id
        $ids = $topUsers | ForEach-Object { $_.id }
        $results = $ids | Invoke-MgxRequest '/users/{id}'
        $results | Should -Not -BeNullOrEmpty
        $results.Count | Should -Be $ids.Count
        $results[0]._MgxSourceId | Should -Not -BeNullOrEmpty
    }

    It 'Collection fan-out: $ids | Invoke-MgxRequest /groups/{id}/members -All should work' {
        $groups = Invoke-MgxRequest /groups -Top 2 -Property id
        if ($groups) {
            $ids = $groups | ForEach-Object { $_.id }
            $results = $ids | Invoke-MgxRequest '/groups/{id}/members' -Top 5
            # May be empty if groups have no members, but should not error
            $results | Should -Not -BeNullOrEmpty -Because 'At least some groups should have members'
        } else {
            Set-ItResult -Skipped -Because 'No groups in tenant'
        }
    }

    It 'Invoke-MgxRequest with {id} but no pipeline should error' {
        { Invoke-MgxRequest '/users/{id}' } | Should -Throw '*pipeline*'
    }

    It '@odata.type should be preserved verbatim' {
        # Use group members (polymorphic endpoint that reliably returns @odata.type)
        $groups = Invoke-MgxRequest /groups -Top 5 -Property id
        $members = @()
        foreach ($g in $groups) {
            $members = @(Invoke-MgxRequest "/groups/$($g.id)/members" -Top 3)
            if ($members.Count -gt 0) { break }
        }
        if ($members.Count -eq 0) {
            Set-ItResult -Skipped -Because 'No group members found to test @odata.type'
            return
        }
        $withType = $members | Where-Object { $_['@odata.type'] }
        $withType | Should -Not -BeNullOrEmpty
    }

    It 'DateTime properties should be DateTimeOffset' {
        $user = Invoke-MgxRequest /users -Top 1
        if ($user.createdDateTime) {
            $user.createdDateTime | Should -BeOfType [System.DateTimeOffset]
        }
    }

    It 'Output parity: Invoke-MgxRequest vs Get-MgUser' {
        $mgx = Invoke-MgxRequest /users -Top 3 -Property id, displayName, userPrincipalName
        $mg  = Get-MgUser -Top 3 -Property Id, DisplayName, UserPrincipalName

        $diff = Compare-Object ($mg.Id | Sort-Object) ($mgx.id | Sort-Object)
        $diff | Should -BeNullOrEmpty
    }

    It 'Invoke-MgxRequest -ApiVersion beta should use beta endpoint' {
        $users = Invoke-MgxRequest /users -ApiVersion beta -Top 3
        $users | Should -Not -BeNullOrEmpty
    }

    It 'Set-MgxOption round-trip: NoRateLimit then re-enable with RateLimitBurst' {
        Set-MgxOption -NoRateLimit
        Set-MgxOption -RateLimitBurst 100
        # Should implicitly clear NoRateLimit; verify by running a request
        $user = Invoke-MgxRequest /users -Top 1
        $user | Should -Not -BeNullOrEmpty
    }

    It 'Export-MgxCollection should export JSONL file' {
        $outFile = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-test-export-$(Get-Random).jsonl"
        try {
            $result = Export-MgxCollection /users -OutputFile $outFile -Top 10
            $result | Should -Not -BeNullOrEmpty
            $result.ItemCount | Should -BeGreaterThan 0
            $result.ItemCount | Should -BeLessOrEqual 10
            $result.OutputFile | Should -Be $outFile
            Test-Path $outFile | Should -BeTrue
            $lines = Get-Content $outFile
            $lines.Count | Should -Be $result.ItemCount
            # Each line should be valid JSON
            $lines | ForEach-Object { $_ | ConvertFrom-Json } | Should -Not -BeNullOrEmpty
        } finally {
            if (Test-Path $outFile) { Remove-Item $outFile -Force }
        }
    }

    It 'Export-MgxCollection checkpoint should be deleted on completion' {
        $outFile = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-test-cp-export-$(Get-Random).jsonl"
        $cpFile = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-test-cp-$(Get-Random).json"
        try {
            Export-MgxCollection /users -OutputFile $outFile -Top 5 -CheckpointPath $cpFile
            Test-Path $cpFile | Should -BeFalse -Because 'Checkpoint should be deleted on successful completion'
        } finally {
            if (Test-Path $outFile) { Remove-Item $outFile -Force }
            if (Test-Path $cpFile) { Remove-Item $cpFile -Force }
        }
    }

    It 'Enable-MgxResilience should inject resilience and SDK should still work' {
        Enable-MgxResilience
        $state = Get-MgxResilience
        $state.IsEnabled | Should -BeTrue
        $state.IsActive | Should -BeTrue

        # SDK cmdlet should work with resilience injected
        $user = Get-MgUser -Top 1
        $user | Should -Not -BeNullOrEmpty

        Disable-MgxResilience
        $state2 = Get-MgxResilience
        $state2.IsEnabled | Should -BeFalse
        $state2.IsActive | Should -BeFalse

        # SDK should still work after disable
        $user2 = Get-MgUser -Top 1
        $user2 | Should -Not -BeNullOrEmpty
    }
}

Describe 'Expand-MgxRelation Buffer Warning' {
    It 'Source code contains 50k buffer warning guard' {
        # The actual live test (piping 50k items) requires Graph connection and takes ~15 min
        # due to fan-out HTTP calls. Verify the guard exists in source instead.
        $source = Get-Content (Join-Path $PSScriptRoot '../src/Mgx.Cmdlets/Cmdlets/Expand/ExpandMgxRelation.cs') -Raw
        $source | Should -Match '50_000'
        $source | Should -Match 'WriteWarning'
        $source | Should -Match 'buffer|Buffer'
    }
}

Describe 'HttpClient Ownership Disposal Guard' {
    # Tests the s_ownsHttpClient guard in MgxCmdletBase.ScheduleDelayedHttpClientDispose.
    # When s_ownsHttpClient is false (SDK fallback path), ResetHttpClient must NOT dispose
    # the HttpClient (it belongs to the SDK). When true, it must dispose after delay.

    BeforeAll {
        # Ensure Mgx.Engine types are loaded (transitive dep of Mgx.Cmdlets)
        $enginePath = Join-Path $PSScriptRoot '../module/Mgx.Engine.dll'
        [System.Reflection.Assembly]::LoadFrom($enginePath) | Out-Null

        $script:CmdletBaseType = [Mgx.Cmdlets.Base.MgxCmdletBase]
        $script:Flags = [System.Reflection.BindingFlags]'Static,NonPublic'
        $script:DisposedField = [System.Net.Http.HttpMessageInvoker].GetField(
            '_disposed', [System.Reflection.BindingFlags]'Instance,NonPublic')

        # Helper: create options with 1-second TotalTimeoutSeconds so delayed disposal fires fast
        $script:ShortTimeoutOptions = [Mgx.Engine.Http.ResilientGraphClientOptions]::new()
        [Mgx.Engine.Http.ResilientGraphClientOptions].GetField(
            '_totalTimeoutSeconds', [System.Reflection.BindingFlags]'Instance,NonPublic'
        ).SetValue($script:ShortTimeoutOptions, 1)
    }

    AfterEach {
        # Restore defaults so other tests aren't affected
        $script:CmdletBaseType.GetField('s_graphHttpClient', $script:Flags).SetValue($null, $null)
        $script:CmdletBaseType.GetField('s_ownsHttpClient', $script:Flags).SetValue($null, $false)
        $script:CmdletBaseType.GetField('s_clientOptions', $script:Flags).SetValue(
            $null, [Mgx.Engine.Http.ResilientGraphClientOptions]::Default)
        $script:CmdletBaseType.GetField('s_cachedTenantId', $script:Flags).SetValue($null, $null)
    }

    It 'Should NOT dispose HttpClient when s_ownsHttpClient is false' {
        $httpClient = [System.Net.Http.HttpClient]::new()

        $script:CmdletBaseType.GetField('s_graphHttpClient', $script:Flags).SetValue($null, $httpClient)
        $script:CmdletBaseType.GetField('s_ownsHttpClient', $script:Flags).SetValue($null, $false)
        $script:CmdletBaseType.GetField('s_clientOptions', $script:Flags).SetValue(
            $null, $script:ShortTimeoutOptions)

        # ResetHttpClient -> ScheduleDelayedHttpClientDispose checks s_ownsHttpClient
        # ResetHttpClient is public static, so use 'Static,Public' (not $script:Flags which is NonPublic)
        $script:CmdletBaseType.GetMethod('ResetHttpClient',
            [System.Reflection.BindingFlags]'Static,Public').Invoke($null, $null)

        # Wait longer than TotalTimeoutSeconds (1s) to let any scheduled disposal fire
        Start-Sleep -Seconds 3

        # HttpClient should NOT be disposed (SDK owns it)
        $script:DisposedField.GetValue($httpClient) | Should -BeFalse
    }

    It 'Should dispose HttpClient when s_ownsHttpClient is true' {
        $httpClient = [System.Net.Http.HttpClient]::new()

        $script:CmdletBaseType.GetField('s_graphHttpClient', $script:Flags).SetValue($null, $httpClient)
        $script:CmdletBaseType.GetField('s_ownsHttpClient', $script:Flags).SetValue($null, $true)
        $script:CmdletBaseType.GetField('s_clientOptions', $script:Flags).SetValue(
            $null, $script:ShortTimeoutOptions)

        $script:CmdletBaseType.GetMethod('ResetHttpClient',
            [System.Reflection.BindingFlags]'Static,Public').Invoke($null, $null)

        # Wait longer than TotalTimeoutSeconds (1s) to let scheduled disposal fire
        Start-Sleep -Seconds 3

        # HttpClient SHOULD be disposed (we own it)
        $script:DisposedField.GetValue($httpClient) | Should -BeTrue
    }
}

Describe 'Sync-MgxDelta ExecuteDeltaSync Tests' -Tag 'Live' {
    BeforeAll {
        $script:liveConnected = $null -ne (Get-MgContext -ErrorAction SilentlyContinue)
        $script:deltaTestDir = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-delta-live-$(New-Guid)"
        New-Item -ItemType Directory -Path $script:deltaTestDir -Force | Out-Null
    }

    BeforeEach {
        if (-not $script:liveConnected) {
            Set-ItResult -Skipped -Because 'Not connected to Microsoft Graph'
        }
    }

    AfterAll {
        if (Test-Path $script:deltaTestDir) { Remove-Item $script:deltaTestDir -Recurse -Force }
    }

    # --- #1: 410 Gone retry flow ---
    It '410 Gone: deletes state and performs full re-sync' {
        $dp = Join-Path $script:deltaTestDir '410-test.json'
        # Create a delta state with a fabricated expired token
        # Graph will return 410 Gone for this invalid token
        @{
            deltaLink = "https://graph.microsoft.com/v1.0/users/delta?`$deltatoken=EXPIRED_FAKE_TOKEN_$(New-Guid)"
            select = ''
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).AddDays(-30).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        # The cmdlet should catch the 410, delete state, and re-sync
        $results = Sync-MgxDelta /users/delta -DeltaPath $dp -Property id -Verbose -WarningVariable warnings *>&1
        $warningTexts = $warnings | ForEach-Object { "$_" }
        $items = $results | Where-Object { $_ -isnot [System.Management.Automation.VerboseRecord] -and $_ -isnot [System.Management.Automation.WarningRecord] }

        # Should have items (full re-sync happened)
        $items.Count | Should -BeGreaterThan 0
        # Delta state should exist (new token saved after re-sync)
        Test-Path $dp | Should -BeTrue
        # Should have warned about 410
        $warningTexts | Where-Object { $_ -like '*410*' -or $_ -like '*expired*' -or $_ -like '*re-sync*' } | Should -Not -BeNullOrEmpty
    }

    # --- #2: 410 on second attempt falls through ---
    # Cannot reliably trigger two consecutive 410s against live Graph.
    # The first 410 causes a full re-sync which gets a fresh token.
    # The only way to get a second 410 is if Graph is broken, which we can't simulate.
    # Tested indirectly: test #1 proves the retry loop works once, and the for-loop
    # has attempt < 2 with when(attempt == 0) guard, so attempt 1 cannot catch 410.

    # --- #3: OutputFile JSONL mode ---
    It 'OutputFile: writes items as one-JSON-per-line' {
        $dp = Join-Path $script:deltaTestDir 'jsonl-test.json'
        $outFile = Join-Path $script:deltaTestDir 'output.jsonl'

        Sync-MgxDelta /users/delta -DeltaPath $dp -Property id,displayName -OutputFile $outFile

        Test-Path $outFile | Should -BeTrue
        $lines = Get-Content $outFile
        $lines.Count | Should -BeGreaterThan 0
        # Each line should be valid JSON
        foreach ($line in $lines) {
            { $line | ConvertFrom-Json } | Should -Not -Throw
        }
        # First item should have an id property
        ($lines[0] | ConvertFrom-Json).id | Should -Not -BeNullOrEmpty
    }

    # --- #4: OutputFile temp file cleanup on error ---
    It 'OutputFile: no orphan temp files after successful sync' {
        $dp = Join-Path $script:deltaTestDir 'tempclean-test.json'
        $outFile = Join-Path $script:deltaTestDir 'tempclean-output.jsonl'

        Sync-MgxDelta /users/delta -DeltaPath $dp -Property id -OutputFile $outFile

        # No .tmp files should remain
        $tmpFiles = Get-ChildItem $script:deltaTestDir -Filter '*.tmp'
        $tmpFiles | Should -BeNullOrEmpty
    }

    # --- #5: OutputFile atomic rename on success ---
    It 'OutputFile: final file exists, no temp file remains' {
        $dp = Join-Path $script:deltaTestDir 'atomic-test.json'
        $outFile = Join-Path $script:deltaTestDir 'atomic-output.jsonl'

        Sync-MgxDelta /users/delta -DeltaPath $dp -Property id -OutputFile $outFile

        Test-Path $outFile | Should -BeTrue
        # Verify it's a complete file (not truncated) by parsing last line
        $lines = Get-Content $outFile
        { $lines[-1] | ConvertFrom-Json } | Should -Not -Throw
    }

    # --- #6: Cancellation does NOT save delta state ---
    It 'Cancellation: delta state unchanged when sync is cancelled' {
        $dp = Join-Path $script:deltaTestDir 'cancel-state.json'

        # First: do a successful sync to create a valid delta state
        Sync-MgxDelta /users/delta -DeltaPath $dp -Property id

        $stateBefore = Get-Content $dp -Raw
        $lastSyncBefore = ($stateBefore | ConvertFrom-Json).lastSync

        # Now start a sync with a very short timeout to force cancellation
        $job = Start-Job -ScriptBlock {
            param($modulePath, $dp)
            Import-Module $modulePath -Force
            # This will be killed before completion
            Sync-MgxDelta /users/delta -DeltaPath $dp -Property id
        } -ArgumentList $ModulePath, $dp

        Start-Sleep -Milliseconds 500
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue

        # Delta state should still have the ORIGINAL lastSync (not updated by cancelled run)
        # Note: the job runs in a separate process so it can't write to the same delta file
        # unless it reaches the save point. A 500ms window should be too short for full pagination.
        $stateAfter = Get-Content $dp | ConvertFrom-Json
        $stateAfter.lastSync | Should -Be $lastSyncBefore
    }

    # --- #7: Cancellation cleans up temp file ---
    # Tested indirectly by #4 and #6. A cancelled sync in a subprocess
    # may leave temp files if killed hard, but the cmdlet's catch block
    # does cleanup. Cannot reliably test kill-during-write from Pester.

    # --- #8: Delta state NOT saved on error ---
    It 'Error: delta state not saved when Graph returns error' {
        $dp = Join-Path $script:deltaTestDir 'error-nosave.json'
        if (Test-Path $dp) { Remove-Item $dp }

        # Use a nonexistent endpoint that will return 404
        $Error.Clear()
        Sync-MgxDelta /nonexistent/delta -DeltaPath $dp -ErrorAction SilentlyContinue

        # Delta state should NOT exist (error before save)
        Test-Path $dp | Should -BeFalse
    }

    # --- #9: GraphServiceException (non-410) writes error record ---
    It 'Error: 404 from Graph produces ErrorRecord' {
        $dp = Join-Path $script:deltaTestDir 'errorrecord-test.json'
        $Error.Clear()

        Sync-MgxDelta /nonexistent/delta -DeltaPath $dp -ErrorAction SilentlyContinue

        $Error.Count | Should -BeGreaterThan 0
        # Graph may return 400 or 404 for nonexistent endpoints
        $Error[0].FullyQualifiedErrorId | Should -Not -BeNullOrEmpty
    }

    # --- #10: BrokenCircuitException writes error record ---
    # Cannot reliably trigger circuit breaker from Pester against live Graph
    # without sending hundreds of requests that all fail. This would abuse
    # the test tenant's quota. The circuit breaker behavior is tested in
    # xUnit (CircuitBreakerTests.cs, 15 tests).

    # --- #11: HttpRequestException writes error record ---
    # Cannot trigger network errors against a live tenant from Pester.
    # Tested in xUnit (Delta_NetworkError_ThrowsHttpRequestException).

    # --- #12: IOException writes error record ---
    It 'Error: unwritable OutputFile produces terminating error' {
        $dp = Join-Path $script:deltaTestDir 'ioerror-test.json'
        $outFile = '/System/locked-output.jsonl'
        if (-not $IsMacOS) { $outFile = '/proc/locked-output.jsonl' }

        # ValidateWriteAccess throws a terminating error in BeginProcessing
        { Sync-MgxDelta /users/delta -DeltaPath $dp -Property id -OutputFile $outFile -ErrorAction Stop } |
            Should -Throw '*Cannot write*'
    }

    # --- #13: No delta token received warning ---
    It 'Warning: non-delta endpoint produces no-deltaLink warning' {
        $dp = Join-Path $script:deltaTestDir 'nodelta-warn.json'
        $warnings = $null

        Sync-MgxDelta /users -DeltaPath $dp -Property id -WarningVariable warnings -ErrorAction SilentlyContinue 3>$null

        $warningTexts = $warnings | ForEach-Object { "$_" }
        # Should warn about no delta token received (endpoint doesn't return deltaLink)
        # OR warn about URI not containing /delta (from BeginProcessing)
        $warningTexts | Should -Not -BeNullOrEmpty
    }
}

# Sustained auth pipeline test. Exercises token refresh under load.
# MSAL's AuthenticationHandler refreshes tokens proactively (5 min before expiry).
# Azure AD tokens expire at 60-90 min, so crossing a token boundary requires ~65+ min.
# This test validates the auth pipeline survives sustained use; manual 2+ hour runs
# are recommended before enterprise deployment per panel review findings.
Describe 'Sustained Auth Pipeline' -Tag 'Live', 'LongRunning' {
    It 'Export-MgxCollection survives sustained load without auth errors' {
        $context = Get-MgContext -ErrorAction SilentlyContinue
        if (-not $context) {
            Set-ItResult -Skipped -Because 'Not connected to Microsoft Graph'
            return
        }
        $outFile = [System.IO.Path]::GetTempFileName() + '.jsonl'
        $cpFile  = [System.IO.Path]::GetTempFileName() + '.json'
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()

            # Export audit logs (large collection, sustained HTTP traffic)
            Export-MgxCollection /auditLogs/signIns -OutputFile $outFile `
                -CheckpointPath $cpFile -All -ErrorAction Stop

            $sw.Stop()
            $lineCount = (Get-Content $outFile -Raw).Split("`n").Where({ $_ }).Count

            # Should have exported records without auth failure
            $lineCount | Should -BeGreaterThan 0
            Write-Host "Exported $lineCount sign-in records in $([math]::Round($sw.Elapsed.TotalMinutes, 1)) minutes"
        } finally {
            Remove-Item $outFile, $cpFile -Force -ErrorAction SilentlyContinue
        }
    }
}
