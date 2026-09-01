using System.Management.Automation;
using System.Reflection;
using Mgx.Cmdlets.Base;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Removes the Polly resilience injection from the Microsoft.Graph SDK's HTTP transport.
/// Restores the original SDK HttpClient that was saved by Enable-MgxResilience.
/// </summary>
[Cmdlet(VerbsLifecycle.Disable, "MgxResilience", SupportsShouldProcess = true)]
public class DisableMgxResilience : PSCmdlet
{
    protected override void ProcessRecord()
    {
        lock (EnableMgxResilience.StateLock)
        {
            if (!EnableMgxResilience.IsEnabled)
            {
                // A wrapper can outlive the state that tracked it. Module removal takes the
                // injection off the session before it drops its references, but a removal that
                // ran while GraphSession was unreachable cannot, and what it leaves behind is a
                // wrapper nothing points at. Reaching it here is the only way back to the
                // genuine client: no other reference to it survives.
                if (EnableMgxResilience.SessionHoldsInjectedWrapper())
                {
                    if (!ShouldProcess("Microsoft.Graph SDK HttpClient",
                        "Restore original SDK HttpClient (remove Polly resilience)"))
                        return;

                    EnableMgxResilience.TryRestoreGenuineSdkClient();
                    WriteVerbose("MgxResilience disabled. The wrapper an earlier import left on "
                        + "the SDK client is gone and SDK cmdlets are restored to original behavior.");
                    return;
                }

                WriteWarning("MgxResilience is not currently enabled.");
                return;
            }

            var originalClient = EnableMgxResilience.OriginalSdkClient;
            if (originalClient == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Original SDK client was not saved. Cannot restore. " +
                        "Restart your PowerShell session to reset."),
                    "OriginalClientMissing", ErrorCategory.InvalidOperation, null));
                return;
            }

            var graphSessionType = MgxCmdletBase.FindType(
                "Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "GraphSession type not found. Module may have been unloaded."),
                    "GraphSessionNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("GraphSession.Instance is null."),
                    "GraphSessionNull", ErrorCategory.InvalidOperation, null));
                return;
            }

            var clientProp = instance.GetType().GetProperty("GraphHttpClient");
            if (clientProp == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "GraphHttpClient property not found on GraphSession. SDK version may be incompatible."),
                    "PropertyNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }

            if (!ShouldProcess("Microsoft.Graph SDK HttpClient",
                "Restore original SDK HttpClient (remove Polly resilience)"))
                return;

            // Verify the current client is actually ours before restoring
            var currentClient = clientProp.GetValue(instance) as HttpClient;
            if (currentClient != null && !ReferenceEquals(currentClient, EnableMgxResilience.ResilientSdkClient))
            {
                WriteWarning("The current GraphHttpClient is not the one injected by Enable-MgxResilience. " +
                           "Another module or Connect-MgGraph may have replaced it. Restoring original anyway.");
            }

            clientProp.SetValue(instance, originalClient);

            // These statics are the last references to the ResilientDelegatingHandler and to
            // the client wrapped around it, and dropping them is what releases those. Not the
            // Polly pipeline: the factory keeps one instance in a static of its own and hands
            // the same one to every client it builds, so it outlives this teardown - which is
            // the point of it. Releasing it would mean ResiliencePipelineFactory.Reset(), and
            // Disable must not call that: the circuit-breaker history, the rate limiter and
            // the learned pacing it holds are shared with mgx's own cmdlets, which keep running
            // after the SDK injection comes off.
            var resilientClient = EnableMgxResilience.ResilientSdkClient;
            EnableMgxResilience.IsEnabled = false;
            EnableMgxResilience.OriginalSdkClient = null;
            EnableMgxResilience.ResilientSdkClient = null;
            // The handler goes with them. It was left set here, so a disabled session still
            // rooted the Polly pipeline and the bridge to the client we just stopped using.
            EnableMgxResilience.ActiveHandler = null;
            // Not disposed. Restoring GraphSession.GraphHttpClient already stops new traffic;
            // disposing would cancel SDK requests still in flight - a paged read running when the
            // user types Disable-MgxResilience would die mid-enumeration rather than finish. It
            // holds no connections of its own either: the SDK client it bridges to owns those,
            // and that client's pool closes its sockets on its own timers.
            _ = resilientClient;

            WriteVerbose("MgxResilience disabled. SDK cmdlets restored to original behavior.");
        }
    }
}
