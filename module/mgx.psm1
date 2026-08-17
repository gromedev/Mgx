# Mgx Module Loader
# Loads Mgx.Cmdlets.dll into the Default ALC.
# ALC dependency isolation is handled by AlcInitializer (IModuleAssemblyInitializer in Mgx.Cmdlets.dll).

$ModuleRoot = $PSScriptRoot

# Load the main cmdlet assembly
$CmdletsDll = Join-Path $ModuleRoot 'Mgx.Cmdlets.dll'
if (Test-Path $CmdletsDll) {
    Import-Module $CmdletsDll
} else {
    Write-Error "Mgx.Cmdlets.dll not found at $CmdletsDll. Did you run the build script?"
}

# Static state cleanup on module removal is handled by AlcInitializer.OnRemove
# (IModuleAssemblyCleanup in Mgx.Cmdlets.dll), NOT here.
#
# It cannot live in this scriptblock: PowerShell runs it after the module's
# IModuleAssemblyCleanup callback, which detaches the ALC resolver. ResetHttpClient
# needs Polly.Core, which is only resolvable through that handler, so calling it from
# here threw and left the module permanently loaded.

# Tab completion for Graph API resource paths
$script:UriCompletions = @(
    @{ Text = 'users';                       Tip = 'All users in the tenant' }
    @{ Text = 'groups';                      Tip = 'All groups' }
    @{ Text = 'applications';                Tip = 'App registrations' }
    @{ Text = 'servicePrincipals';           Tip = 'Enterprise apps / service principals' }
    @{ Text = 'devices';                     Tip = 'Registered devices' }
    @{ Text = 'directoryRoles';              Tip = 'Directory roles' }
    @{ Text = 'domains';                     Tip = 'Verified domains' }
    @{ Text = 'organization';                Tip = 'Tenant info' }
    @{ Text = 'subscribedSkus';              Tip = 'License SKUs' }
    @{ Text = 'teams';                       Tip = 'Teams' }
    @{ Text = 'sites';                       Tip = 'SharePoint sites' }
    @{ Text = 'drives';                      Tip = 'OneDrive drives' }
    @{ Text = 'auditLogs/signIns';           Tip = 'Sign-in logs' }
    @{ Text = 'auditLogs/directoryAudits';   Tip = 'Directory audit logs' }
    @{ Text = 'me/drive/root/delta';         Tip = 'Drive delta: your OneDrive' }
    @{ Text = 'users/delta';                 Tip = 'Delta: user changes' }
    @{ Text = 'groups/delta';                Tip = 'Delta: group changes' }
    @{ Text = 'servicePrincipals/delta';     Tip = 'Delta: service principal changes' }
)

$script:UriCompleter = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    $script:UriCompletions | Where-Object { $_.Text -like "$wordToComplete*" } | ForEach-Object {
        [System.Management.Automation.CompletionResult]::new($_.Text, $_.Text, 'ParameterValue', $_.Tip)
    }
}

foreach ($cmd in 'Invoke-MgxRequest', 'Invoke-MgxBatchRequest', 'Export-MgxCollection', 'Expand-MgxRelation', 'Sync-MgxDelta') {
    Register-ArgumentCompleter -CommandName $cmd -ParameterName Uri -ScriptBlock $script:UriCompleter
}
