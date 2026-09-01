using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Points MgxCmdletBase at a mock transport for the life of the scope and puts every static it
/// touched back the way it found it, so a later test in the Pipeline collection that drives a
/// cmdlet without injecting cannot inherit this one's wire.
///
/// The seam sits above the auth probe in GetClient, so a test shell needs no Graph session and no
/// matching fingerprint. The fields are internal and assigned directly rather than reflected:
/// renaming one is a compile error here instead of a NullReferenceException at run time.
///
/// Every scope resets ResiliencePipelineFactory on entry and exit. Retry-count assertions read
/// the pipeline the factory hands out, so a pipeline built under another test's MaxRetryAttempts
/// would change what those counts mean.
/// </summary>
internal sealed class MgxTransportScope : IDisposable
{
    private readonly HttpClient? _createdClient;
    private readonly HttpClient? _previousTransport;
    private readonly bool _previousOwned;
    private readonly ResilientGraphClientOptions _previousOptions;
    private readonly string _previousEndpoint;

    private MgxTransportScope(HttpClient client, bool createdHere, bool owned,
        ResilientGraphClientOptions options, string endpoint)
    {
        _createdClient = createdHere ? client : null;
        _previousTransport = MgxCmdletBase.s_testTransport;
        _previousOwned = MgxCmdletBase.s_testTransportOwned;
        _previousOptions = MgxCmdletBase.s_clientOptions;
        _previousEndpoint = MgxCmdletBase.s_graphEndpoint;

        ResiliencePipelineFactory.Reset();
        MgxCmdletBase.s_clientOptions = options;
        MgxCmdletBase.s_graphEndpoint = endpoint;
        Publish(client, owned);
    }

    /// <summary>
    /// Arms a transport: the transport itself, then the epoch that retires whatever preceded it.
    /// GetClient takes the epoch, the transport, then the epoch again, and rereads on a change -
    /// so the transport going first is what lets an unchanged epoch mean the two belong together.
    /// Bumping first would leave a window in which the new epoch is live over the old transport,
    /// which a reader cannot tell from a settled state.
    /// <para>
    /// Entering and leaving both go through here, so the two directions cannot drift apart.
    /// </para>
    /// </summary>
    private void Publish(HttpClient? transport, bool owned)
    {
        MgxCmdletBase.s_testTransportOwned = owned;
        MgxCmdletBase.s_testTransport = transport;
        BetweenPublishWrites?.Invoke();
        Interlocked.Increment(ref MgxCmdletBase.s_testTransportEpoch);
    }

    /// <summary>
    /// Runs between the two writes above, which is the only point from which their order is
    /// visible. Set by the one test that pins it; null for every other scope, so a test running
    /// in another collection at the same time is unaffected.
    /// </summary>
    internal Action? BetweenPublishWrites { get; set; }

    /// <summary>The options a scope installs unless the caller passes its own.</summary>
    internal static ResilientGraphClientOptions DefaultOptions =>
        new() { NoRateLimit = true, MaxRetryAttempts = 1 };

    /// <summary>
    /// Arms the seam with an HttpClient over <paramref name="wire"/>. The scope owns that client
    /// and disposes it on exit.
    /// </summary>
    internal static MgxTransportScope Inject(
        HttpMessageHandler wire,
        bool owned = false,
        ResilientGraphClientOptions? options = null,
        string endpoint = "https://graph.microsoft.com") =>
        new(new HttpClient(wire), createdHere: true, owned, options ?? DefaultOptions, endpoint);

    /// <summary>
    /// Arms the seam with a caller-owned HttpClient, for a test that needs the same instance
    /// across several scopes or disposes it itself.
    /// </summary>
    internal static MgxTransportScope Inject(
        HttpClient client,
        bool owned = false,
        ResilientGraphClientOptions? options = null,
        string endpoint = "https://graph.microsoft.com") =>
        new(client, createdHere: false, owned, options ?? DefaultOptions, endpoint);

    /// <summary>
    /// Replaces the options the armed transport runs under, without rebuilding the transport.
    /// Bumps the epoch, so a cmdlet instance holding a client from before the change rebuilds.
    /// </summary>
    internal static void SetOptions(ResilientGraphClientOptions options)
    {
        ResiliencePipelineFactory.Reset();
        MgxCmdletBase.s_clientOptions = options;
        Interlocked.Increment(ref MgxCmdletBase.s_testTransportEpoch);
    }

    public void Dispose()
    {
        Publish(_previousTransport, _previousOwned);
        MgxCmdletBase.s_graphEndpoint = _previousEndpoint;
        MgxCmdletBase.s_clientOptions = _previousOptions;
        ResiliencePipelineFactory.Reset();
        _createdClient?.Dispose();
    }
}
