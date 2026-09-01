using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;
using Microsoft.Graph.PowerShell.Authentication;

namespace Mgx.IntegrationTests;

/// <summary>
/// Arms the fake GraphSession for the life of the scope and puts the process back the way it was
/// on dispose: no session instance, no injection state, no cached mgx client, a reset pipeline
/// factory. Without the restore a later test would find a session where the suite assumes none.
/// </summary>
internal sealed class GraphSessionScope : IDisposable
{
    internal GraphSession Session { get; }

    private readonly ResilientGraphClientOptions _previousOptions;
    private readonly string _previousEndpoint;

    private GraphSessionScope(HttpClient? graphHttpClient, object? authContext)
    {
        // A scenario that drives the real GetClient sets both of these - the options because it
        // chooses what the limiter under test does, the endpoint because GetClient reads it off
        // the session it is about to arm. Neither belongs to the tests that run after it.
        _previousOptions = MgxCmdletBase.s_clientOptions;
        _previousEndpoint = MgxCmdletBase.s_graphEndpoint;

        MgxCmdletBase.ResetHttpClient();
        Session = GraphSession.Attach();
        Session.GraphHttpClient = graphHttpClient;
        Session.AuthContext = authContext;
        Session.Environment = new FakeGraphEnvironment();
    }

    internal static GraphSessionScope Arm(HttpClient? graphHttpClient = null, object? authContext = null) =>
        new(graphHttpClient, authContext);

    /// <summary>An AuthContext-shaped object. BuildAuthFingerprint reads these members by name.</summary>
    internal static object AuthContextFor(string tenantId, string clientId) => new
    {
        TenantId = tenantId,
        ClientId = clientId,
        AuthType = "AppOnly",
        Environment = "Global"
    };

    public void Dispose()
    {
        GraphSession.Detach();
        EnableMgxResilience.ReleaseInjection();
        MgxCmdletBase.s_clientOptions = _previousOptions;
        MgxCmdletBase.s_graphEndpoint = _previousEndpoint;
        MgxCmdletBase.ResetHttpClient();
        ResiliencePipelineFactory.Reset();
    }
}
