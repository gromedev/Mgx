#Requires -Modules Pester

<#
    Manifest and packaging contract for the built module in module/.
    These run against build output, so ./build.ps1 must have completed first.
#>

BeforeAll {
    Import-Module -Name (Join-Path (Split-Path -Parent $PSScriptRoot) 'TestHarness.psm1') -Force
    $script:Paths = Get-MgxTestPath
    $script:Manifest = Test-ModuleManifest -Path $script:Paths.ManifestPath -ErrorAction Stop
}

Describe 'mgx module manifest' {
    It 'is a valid module manifest' {
        $script:Manifest | Should -Not -BeNullOrEmpty
        $script:Manifest.Name | Should -Be 'mgx'
    }

    It 'declares a parsable version' {
        $script:Manifest.Version | Should -BeOfType [System.Version]
    }

    It 'targets PowerShell 7 Core only' {
        # The module is built for net9.0 and cannot load in Windows PowerShell 5.1
        $script:Manifest.PowerShellVersion | Should -BeGreaterOrEqual ([Version]'7.0')
        $script:Manifest.CompatiblePSEditions | Should -Contain 'Core'
        $script:Manifest.CompatiblePSEditions | Should -Not -Contain 'Desktop'
    }

    It 'pre-loads Mgx.Engine.dll via RequiredAssemblies' {
        # Without this, MgxTelemetrySummary resolves into a different load context
        # and Get-MgxTelemetry fails at JIT time with TypeLoadException
        $script:Manifest.RequiredAssemblies | Should -Contain 'Mgx.Engine.dll'
    }

    It 'requires Microsoft.Graph.Authentication' {
        # Auth and the Graph endpoint are resolved from the SDK's GraphSession
        $script:Manifest.RequiredModules.Name | Should -Contain 'Microsoft.Graph.Authentication'
    }

    It 'exports cmdlets and no functions' {
        $script:Manifest.ExportedCmdlets.Count | Should -BeGreaterThan 0
        $script:Manifest.ExportedFunctions.Count | Should -Be 0
    }

    It 'has release notes that match the manifest version' {
        $notes = $script:Manifest.PrivateData.PSData.ReleaseNotes
        $notes | Should -Not -BeNullOrEmpty

        $expected = 'v{0}.{1}.{2}' -f $script:Manifest.Version.Major,
                                      $script:Manifest.Version.Minor,
                                      $script:Manifest.Version.Build
        $notes | Should -Match ([Regex]::Escape($expected))
    }
}

Describe 'CHANGELOG' {
    It 'documents the version in the manifest' {
        # Catches a version bump that forgot the changelog entry
        $changelog = Get-Content -Path (Join-Path $script:Paths.RepoRoot 'CHANGELOG.md') -Raw
        $version = '{0}.{1}.{2}' -f $script:Manifest.Version.Major,
                                    $script:Manifest.Version.Minor,
                                    $script:Manifest.Version.Build

        $changelog | Should -Match ('##\s+{0}' -f [Regex]::Escape($version))
    }
}

Describe 'Module payload' {
    It 'stages every assembly the manifest and loader depend on' {
        foreach ($file in @('Mgx.Cmdlets.dll', 'Mgx.Engine.dll', 'mgx.psm1', 'mgx.Format.ps1xml'))
        {
            Join-Path $script:Paths.ModuleRoot $file | Should -Exist
        }
    }

    It 'isolates third-party dependencies under Dependencies/' {
        # Polly and RateLimiting load through the ALC Resolving handler
        foreach ($file in @('Polly.Core.dll', 'System.Threading.RateLimiting.dll'))
        {
            Join-Path $script:Paths.ModuleRoot 'Dependencies' $file | Should -Exist
        }
    }

    It 'keeps Mgx assemblies out of Dependencies/' {
        # An Mgx assembly in Dependencies/ would load into the isolated ALC and
        # produce TypeLoadException across the assembly boundary
        $orphans = Get-ChildItem -Path (Join-Path $script:Paths.ModuleRoot 'Dependencies') -Filter 'Mgx.*.dll' -ErrorAction SilentlyContinue
        $orphans | Should -BeNullOrEmpty
    }

    It 'names the root files exactly after the module' {
        # On case-sensitive filesystems PowerShell resolves the module directory by name and
        # then requires <name>.psd1 to match byte for byte, so any casing drift between the
        # manifest name and these file names breaks Import-Module on Linux.
        $moduleName = $script:Paths.ModuleName

        foreach ($file in @("$moduleName.psd1", "$moduleName.psm1", "$moduleName.Format.ps1xml"))
        {
            $actual = (Get-ChildItem -Path $script:Paths.ModuleRoot -Filter $file).Name
            $actual | Should -BeExactly $file
        }
    }

    It 'points RootModule and FormatsToProcess at the module files' {
        $script:Manifest.RootModule | Should -BeExactly 'mgx.psm1'
        $script:Manifest.ExportedFormatFiles |
            ForEach-Object { Split-Path -Leaf $_ } |
            Should -Contain 'mgx.Format.ps1xml'
    }
}

Describe 'Format file' {
    BeforeAll {
        $script:FormatXml = [xml](Get-Content -Path $script:Paths.FormatPath -Raw)
    }

    It 'is well-formed XML with at least one view' {
        $script:FormatXml.Configuration.ViewDefinitions.View.Count | Should -BeGreaterThan 0
    }

    It 'declares only views the module actually emits' {
        # Graph entity views (Mgx.User etc.) select on the synthetic PSTypeName stamped
        # from @odata.type; everything else must be a real Mgx.Cmdlets.Models type.
        $declared = $script:FormatXml.Configuration.ViewDefinitions.View.ViewSelectedBy.TypeName

        foreach ($typeName in $declared)
        {
            $typeName | Should -Match '^Mgx\.(Cmdlets\.Models\.)?[A-Za-z]+$'
        }
    }
}
