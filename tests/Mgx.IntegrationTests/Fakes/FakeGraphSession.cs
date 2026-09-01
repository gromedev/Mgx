namespace Microsoft.Graph.PowerShell.Authentication;

/// <summary>
/// Stands in for the Graph SDK's GraphSession singleton. MgxCmdletBase.FindType resolves that
/// type by full name across the loaded assemblies, so a type of this name declared here is what
/// the injection paths find in a process where the SDK is absent.
///
/// Everything mgx reads off the real session is duck-typed reflection - Instance, AuthContext,
/// GraphHttpClient, Environment - so matching the member names is the whole contract.
///
/// Instance is null until a test arms it through Mgx.IntegrationTests.GraphSessionScope, and the
/// scope detaches it again on dispose. Nothing else in the suite sees a session.
/// </summary>
internal sealed class GraphSession
{
    private static GraphSession? s_instance;

    public static GraphSession? Instance => s_instance;

    public object? AuthContext { get; set; }

    public HttpClient? GraphHttpClient { get; set; }

    public object? Environment { get; set; }

    internal static GraphSession Attach()
    {
        var session = new GraphSession();
        s_instance = session;
        return session;
    }

    internal static void Detach() => s_instance = null;
}

/// <summary>The members mgx reads off GraphSession.Environment, and nothing more.</summary>
internal sealed class FakeGraphEnvironment
{
    public string GraphEndpoint { get; set; } = "https://graph.microsoft.com";

    public string AzureADEndpoint { get; set; } = "https://login.microsoftonline.com";
}
