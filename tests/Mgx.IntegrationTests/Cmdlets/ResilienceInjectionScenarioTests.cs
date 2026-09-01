using System.Net;
using System.Reflection;
using Mgx.Cmdlets;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// The existing suites cover request-level behavior through the wrap - disarming Kiota's inner
/// retry handler across 429 and 503, correlation-id stamping, oversized bodies, lazy one-shot
/// option resolution - and two properties of the wrapper client. None of the injection
/// scenarios is covered as a scenario, because each needs a GraphSession and this process has
/// no Graph SDK. GraphSessionScope supplies one.
///
/// Coexistence with Az/PnP/Exchange is not here: module load and ALC resolution are
/// irreversible within a process, so that belongs to the out-of-process ecosystem matrix.
/// </summary>
[Collection("Pipeline")]
public class ResilienceInjectionScenarioTests
{
    private static HttpClient? BuildWrapper(HttpClient sdkClient, List<string> warnings) =>
        EnableMgxResilience.BuildResilientSdkClient(sdkClient, warnings.Add);

    /// <summary>Puts the process in the state Enable-MgxResilience leaves behind on success.</summary>
    private static HttpClient Enable(GraphSessionScope scope, HttpClient sdkClient)
    {
        var wrapper = BuildWrapper(sdkClient, [])!;
        EnableMgxResilience.OriginalSdkClient = sdkClient;
        EnableMgxResilience.ResilientSdkClient = wrapper;
        EnableMgxResilience.IsEnabled = true;
        scope.Session.GraphHttpClient = wrapper;
        return wrapper;
    }

    /// <summary>
    /// Asserts that <paramref name="wrapper"/> is exactly one resilience layer over
    /// <paramref name="genuine"/>. A second layer answers requests as happily as one, so nothing
    /// above the wire shows it: each layer retries the one beneath it, so a throttled request
    /// multiplies its attempts, telemetry counts each attempt once per layer, and the pacer
    /// halves once per layer.
    /// </summary>
    private static void AssertOneLayerOver(HttpClient wrapper, HttpClient genuine, string context = "")
    {
        var (layers, innermost) = MeasureWrap(wrapper);
        Assert.True(layers == 1, $"{context}the wrap is {layers} layers deep");
        Assert.True(ReferenceEquals(genuine, innermost),
            $"{context}the wrap bridges to something other than the genuine SDK client");
    }

    /// <summary>
    /// Walks the handler chain to the client at the bottom of it, counting resilience layers on
    /// the way. The object graph, not mgx's bookkeeping about it: what the wire sees is the chain.
    /// </summary>
    private static (int Layers, HttpClient Innermost) MeasureWrap(HttpClient client)
    {
        var layers = 0;
        while (HandlerOf(client) is ResilientDelegatingHandler resilient)
        {
            layers++;
            var bridge = resilient.InnerHandler;
            Assert.NotNull(bridge);
            var target = bridge!.GetType().GetField("_sdkClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(target != null, $"{bridge.GetType().Name} is not the SDK bridge handler");
            client = (HttpClient)target!.GetValue(bridge)!;
        }
        return (layers, client);
    }

    /// <summary>The handler an HttpClient sends through; it keeps it in a private field.</summary>
    private static HttpMessageHandler? HandlerOf(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, "HttpMessageInvoker no longer keeps its handler in _handler");
        return (HttpMessageHandler?)field!.GetValue(client);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{"value":[]}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    // --- 1. the SDK replaced our client ---

    [Fact]
    public void A_replaced_graph_http_client_is_wrapped_again_rather_than_left_raw()
    {
        using var scope = GraphSessionScope.Arm();
        using var first = new HttpClient(new OkHandler()) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var wrapper = Enable(scope, first);

        // Connect-MgGraph builds a fresh SDK client and hands it to the session, discarding ours.
        using var replacement = new HttpClient(new OkHandler()) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        scope.Session.GraphHttpClient = replacement;

        EnableMgxResilience.RefreshInjectedClient(_ => { }, _ => { });

        var active = scope.Session.GraphHttpClient;
        Assert.True(EnableMgxResilience.IsEnabled, "the wrap was dropped instead of re-injected");
        Assert.NotSame(replacement, active);        // the caller is not left on the raw SDK client
        Assert.NotSame(wrapper, active);            // nor on the wrapper that bridged to the old one
        Assert.Same(active, EnableMgxResilience.ResilientSdkClient);
        Assert.Same(replacement, EnableMgxResilience.OriginalSdkClient);
    }

    // --- 2. reconnect versus credential change ---

    [Fact]
    public void A_same_identity_reconnect_keeps_the_limiter_a_credential_change_resets_it()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.OK, TestData.SingleUser);
        using var sdkClient = new HttpClient(wire);

        const string tenant = "11111111-1111-1111-1111-111111111111";
        const string appA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string appB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        using var scope = GraphSessionScope.Arm(sdkClient, GraphSessionScope.AuthContextFor(tenant, appA));

        // No transport seam here: this is about the identity check inside GetClient, which the
        // seam sits above. BuildCleanHttpClient cannot run without the SDK's auth helpers, so
        // GetClient falls back to the session's own client - the borrowed-transport path.
        // A real limiter, not NoRateLimit: the property under test is that the warm limiter
        // instance survives, and NoRateLimit leaves the factory with none to keep. The burst
        // covers every request this test sends, so nothing waits on it.
        var options = new ResilientGraphClientOptions
        {
            MaxRetryAttempts = 1,
            RateLimitBurst = 50,
            RateLimitPerSecond = 50,
            NoAdaptivePacing = true
        };
        MgxCmdletBase.SetClientOptions(options);
        ResiliencePipelineFactory.Reset();

        Run();
        var afterFirst = ResiliencePipelineFactory.GetOrCreate(options).RateLimiter;

        // Reconnect: a new AuthContext object carrying the same credentials. The instance
        // changed, so the client is rebuilt - but the identity did not, so the limiter that has
        // already learned this tenant's ceiling must survive rather than earn a fresh burst.
        scope.Session.AuthContext = GraphSessionScope.AuthContextFor(tenant, appA);
        Run();
        Assert.Same(afterFirst, ResiliencePipelineFactory.GetOrCreate(options).RateLimiter);

        // A different application on the same tenant is a different credential.
        scope.Session.AuthContext = GraphSessionScope.AuthContextFor(tenant, appB);
        Run();
        Assert.NotSame(afterFirst, ResiliencePipelineFactory.GetOrCreate(options).RateLimiter);

        void Run()
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users/u1")
              .AddParameter("WarningAction", System.Management.Automation.ActionPreference.SilentlyContinue);
            var output = ps.Invoke();
            Assert.True(output.Count > 0,
                "the request did not reach the session's client: "
                + string.Join(" | ", ps.Streams.Error.Select(e => e.FullyQualifiedErrorId)));
        }
    }

    // --- 3. one wrapper at a time ---

    [Fact]
    public void A_second_sdk_client_leaves_exactly_one_wrapper_armed()
    {
        using var scope = GraphSessionScope.Arm();
        using var first = new HttpClient(new OkHandler());
        Enable(scope, first);
        var firstHandler = EnableMgxResilience.ActiveHandler;
        Assert.NotNull(firstHandler);

        using var second = new HttpClient(new OkHandler());
        scope.Session.GraphHttpClient = second;
        EnableMgxResilience.RefreshInjectedClient(_ => { }, _ => { });

        // ActiveHandler is what Disable-MgxResilience and module teardown reach for. Two armed
        // handlers would mean one of them keeps a pipeline and a bridge alive unreachably.
        Assert.NotNull(EnableMgxResilience.ActiveHandler);
        Assert.NotSame(firstHandler, EnableMgxResilience.ActiveHandler);
        Assert.Same(scope.Session.GraphHttpClient, EnableMgxResilience.ResilientSdkClient);
        Assert.Same(second, EnableMgxResilience.OriginalSdkClient);
    }

    // --- 4. module removal ---

    [Fact]
    public void Module_removal_puts_the_session_back_on_the_genuine_client()
    {
        using var scope = GraphSessionScope.Arm();
        using var sdkClient = new HttpClient(new OkHandler())
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var wrapper = Enable(scope, sdkClient);
        var handlerBeforeRemoval = EnableMgxResilience.ActiveHandler;
        Assert.NotNull(handlerBeforeRemoval);

        // The teardown itself, not a copy of it. AlcInitializer.OnRemove is this call plus
        // DetachAssemblyLoadHandler.
        AlcInitializer.ReleaseStaticState();

        Assert.Null(EnableMgxResilience.ActiveHandler);
        Assert.Null(EnableMgxResilience.ResilientSdkClient);
        Assert.Null(EnableMgxResilience.OriginalSdkClient);
        Assert.False(EnableMgxResilience.IsEnabled);

        // What the teardown leaves installed is what the SDK goes on sending through, so the
        // session has to be back on the genuine client before the references go. A wrapper left
        // there routes every SDK request through a handler belonging to a module that is no
        // longer loaded, for as long as nobody imports mgx again.
        Assert.Same(sdkClient, scope.Session.GraphHttpClient);

        var warnings = EnableThroughTheCmdlet();

        Assert.Empty(warnings);
        Assert.True(EnableMgxResilience.IsEnabled);
        Assert.NotNull(EnableMgxResilience.ActiveHandler);
        Assert.NotSame(handlerBeforeRemoval, EnableMgxResilience.ActiveHandler);
        Assert.NotSame(wrapper, EnableMgxResilience.ResilientSdkClient);
        Assert.Same(scope.Session.GraphHttpClient, EnableMgxResilience.ResilientSdkClient);
        Assert.Same(sdkClient, EnableMgxResilience.OriginalSdkClient);
        AssertOneLayerOver(EnableMgxResilience.ResilientSdkClient!, sdkClient);
    }

    [Fact]
    public void Three_remove_and_re_enable_cycles_leave_the_wrap_one_layer_deep()
    {
        using var scope = GraphSessionScope.Arm();
        using var sdkClient = new HttpClient(new OkHandler())
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        scope.Session.GraphHttpClient = sdkClient;

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var where = $"cycle {cycle}: ";
            Assert.Empty(EnableThroughTheCmdlet());

            var wrapper = EnableMgxResilience.ResilientSdkClient;
            Assert.True(wrapper != null, $"{where}resilience was not injected");
            Assert.True(ReferenceEquals(wrapper, scope.Session.GraphHttpClient),
                $"{where}the session is not on the wrapper mgx built");
            Assert.True(ReferenceEquals(sdkClient, EnableMgxResilience.OriginalSdkClient),
                $"{where}the client Disable-MgxResilience would restore is not the genuine one");
            AssertOneLayerOver(wrapper!, sdkClient, where);

            // Remove-Module. The next iteration's Enable-MgxResilience is the re-import: it runs
            // the cmdlet against whatever this teardown left on the session.
            AlcInitializer.ReleaseStaticState();
            Assert.True(ReferenceEquals(sdkClient, scope.Session.GraphHttpClient),
                $"{where}removal left something other than the genuine client on the session");
        }
    }

    /// <summary>
    /// Runs Enable-MgxResilience where PowerShell runs it. The cmdlet reads the session through
    /// the same reflection a re-imported module would, so what the previous import left behind is
    /// what it decides against.
    /// </summary>
    private static IReadOnlyList<string> EnableThroughTheCmdlet() => RunCmdlet("Enable-MgxResilience");

    private static IReadOnlyList<string> DisableThroughTheCmdlet() => RunCmdlet("Disable-MgxResilience");

    private static IReadOnlyList<string> RunCmdlet(string name)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(EnableMgxResilience).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand(name);
        ps.Invoke();

        Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    // --- 5. taking the injection off again ---

    [Fact]
    public void Disable_after_a_module_removal_says_so_and_leaves_the_genuine_client()
    {
        using var scope = GraphSessionScope.Arm();
        using var sdkClient = new HttpClient(new OkHandler())
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        Enable(scope, sdkClient);

        AlcInitializer.ReleaseStaticState();

        var warnings = DisableThroughTheCmdlet();

        // Removal already unwound the injection, so there is nothing left to disable and saying
        // so is the honest answer - but only because the session is on the genuine client.
        Assert.Contains(warnings, w => w.Contains("not currently enabled", StringComparison.Ordinal));
        Assert.Same(sdkClient, scope.Session.GraphHttpClient);
    }

    [Fact]
    public void Disable_takes_off_a_wrapper_that_no_state_points_at_any_more()
    {
        using var scope = GraphSessionScope.Arm();
        using var sdkClient = new HttpClient(new OkHandler())
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var wrapper = Enable(scope, sdkClient);

        // What a removal that could not reach GraphSession leaves: the wrapper is installed and
        // every reference mgx held to it, and to the client under it, is gone.
        EnableMgxResilience.IsEnabled = false;
        EnableMgxResilience.ActiveHandler = null;
        EnableMgxResilience.ResilientSdkClient = null;
        EnableMgxResilience.OriginalSdkClient = null;
        Assert.Same(wrapper, scope.Session.GraphHttpClient);

        var warnings = DisableThroughTheCmdlet();

        Assert.Empty(warnings);
        Assert.Same(sdkClient, scope.Session.GraphHttpClient);
    }

    [Fact]
    public void Disable_after_a_re_import_restores_the_genuine_client_not_the_previous_wrapper()
    {
        using var scope = GraphSessionScope.Arm();
        using var sdkClient = new HttpClient(new OkHandler())
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var firstWrapper = Enable(scope, sdkClient);

        AlcInitializer.ReleaseStaticState();
        Assert.Empty(EnableThroughTheCmdlet());
        Assert.NotSame(firstWrapper, scope.Session.GraphHttpClient);

        var warnings = DisableThroughTheCmdlet();

        Assert.Empty(warnings);
        Assert.Same(sdkClient, scope.Session.GraphHttpClient);
        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Null(EnableMgxResilience.ResilientSdkClient);
        Assert.Null(EnableMgxResilience.OriginalSdkClient);
        Assert.Null(EnableMgxResilience.ActiveHandler);
    }

    // --- 6. the option factory the cmdlet installs ---

    [Fact]
    public async Task The_wrap_the_cmdlet_builds_sends_the_request_its_option_factory_annotates()
    {
        // The chain the cmdlet builds is ResilientDelegatingHandler -> bridge -> SDK client, and
        // nothing in it consumes the retry option when the SDK's handlers are not there. The
        // factory is the cmdlet's own, so whatever it does on this path - answer, or throw over a
        // type the process has not loaded - is what the handler has to absorb, and the request
        // has to go out either way.
        var wire = new OkHandler();
        using var sdkClient = new HttpClient(wire) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var warnings = new List<string>();

        var wrapper = BuildWrapper(sdkClient, warnings);
        try
        {
            Assert.NotNull(wrapper);
            Assert.Empty(warnings);

            // The factory the cmdlet installs is BuildInnerRetryOverride itself, so the guard
            // that already covers a throwing factory covers the production chain too.
            var handler = EnableMgxResilience.ActiveHandler;
            Assert.NotNull(handler);
            var factory = handler.AdditionalRequestOptionsFactory;
            Assert.NotNull(factory);
            Assert.Equal(nameof(EnableMgxResilience.BuildInnerRetryOverride), factory.Method.Name);

            // Which outcome this run gets is not this file's to choose: BuildInnerRetryOverride
            // resolves the SDK's option type across the loaded assemblies, so it depends on
            // whether anything has pulled Kiota in yet. Asserting only the request would pass
            // under either without saying which happened, so the handler's own note decides.
            var notes = new List<string>();
            handler.VerboseWriter = notes.Add;
            var resolved = Record.Exception(() => factory());

            using var response = await wrapper!.GetAsync("users");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, wire.Requests);

            var absorbed = notes.Where(note =>
                note.StartsWith("Could not configure the SDK's retry option", StringComparison.Ordinal)).ToList();
            if (resolved == null)
            {
                Assert.Empty(absorbed);
                Assert.NotEmpty(factory()!);
            }
            else
            {
                Assert.Single(absorbed);
                Assert.Contains(resolved.Message, absorbed[0], StringComparison.Ordinal);
            }
        }
        finally
        {
            wrapper?.Dispose();
            EnableMgxResilience.ActiveHandler = null;
            ResiliencePipelineFactory.Reset();
        }
    }

    // --- 7. mgx's own requests while the injection is armed ---

    /// <summary>
    /// Enable-MgxResilience leaves a wrapper of ours on GraphSession.GraphHttpClient. When
    /// BuildCleanHttpClient cannot run - the Graph SDK's auth helpers are not in this process, and
    /// in production any SDK build that moved them, or any throw out of provider construction -
    /// GetClient borrows the session's client instead, and borrowing that wrapper puts mgx's own
    /// pipeline on top of the one already inside it.
    /// <para>
    /// Two pipelines answer a request as happily as one, so nothing above the wire says which is
    /// running. What multiplies is the retry budget the caller configured: each layer retries the
    /// one beneath it, so a throttled request sends the square of its attempts at a service that
    /// is already throttling, and telemetry books each attempt once per layer.
    /// </para>
    /// </summary>
    [Fact]
    public void The_borrowed_session_client_is_the_genuine_one_not_the_wrapper_over_it()
    {
        var wire = new MockHttpHandler();
        wire.SetDefaultResponse(HttpStatusCode.TooManyRequests,
            """{"error":{"code":"TooManyRequests","message":"slow down"}}""",
            new Dictionary<string, string> { ["Retry-After"] = "0" });
        using var sdkClient = new HttpClient(wire)
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };

        using var scope = GraphSessionScope.Arm(
            sdkClient,
            GraphSessionScope.AuthContextFor(
                "11111111-1111-1111-1111-111111111111", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        // Two retries, so one pipeline serves three attempts to a request that keeps being
        // refused and two serve nine - far enough apart that no timing or ordering detail reads
        // as the other. The breaker never evaluates at either count, so the second invocation
        // reaches the wire on the same terms as the first. Options before Enable: the wrapper
        // captures a pipeline when it is built, and it has to be the one under test.
        MgxCmdletBase.SetClientOptions(new ResilientGraphClientOptions
        {
            MaxRetryAttempts = 2,
            MaxRetryAfterSeconds = 1,
            NoRateLimit = true,
            NoAdaptivePacing = true,
            CircuitBreakerMinThroughput = 1000
        });
        ResiliencePipelineFactory.Reset();

        var wrapper = Enable(scope, sdkClient);

        // No transport seam armed: the borrow is inside GetClient, which the seam sits above.
        var warnings = new List<string>();
        Invoke(warnings);
        var firstRun = wire.RequestCount;
        Invoke(warnings);
        var secondRun = wire.RequestCount - firstRun;

        Assert.Same(wrapper, scope.Session.GraphHttpClient);   // the injection is still armed
        Assert.Equal(3, firstRun);
        Assert.Equal(3, secondRun);

        // The transport GetClient built its ResilientGraphClient on, measured on the object graph
        // rather than on mgx's bookkeeping about it. That client is the one resilience layer this
        // request path is meant to have, so anything under the transport is a second one.
        var (layers, innermost) = MeasureWrap(BorrowedTransport());
        Assert.True(layers == 0, $"mgx borrowed a transport {layers} resilience layers deep");
        Assert.Same(sdkClient, innermost);

        // The borrowed client is compared against the session's on every invocation, to catch a
        // Connect-MgGraph that swapped it. Comparing the wrapper the session holds against the
        // genuine client mgx borrowed reads as a swap every time: it rebuilds, and warns again,
        // for the rest of the session.
        Assert.Equal(1, warnings.Count(w =>
            w.Contains("Falling back to SDK client", StringComparison.Ordinal)));

        void Invoke(List<string> collected)
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("Invoke-MgxRequest -Uri /users/u1 -ErrorAction SilentlyContinue | Out-Null");
            ps.Invoke();
            collected.AddRange(ps.Streams.Warning.Select(w => w.Message));
        }
    }

    /// <summary>
    /// What the teardown releases, and what it leaves standing. The handler and the client
    /// wrapped around it go with the statics; the Polly pipeline does not - the factory keeps
    /// one instance and hands the same one to every client it builds, so it survives an SDK
    /// injection being taken off. Releasing it would mean Reset(), and that would take the
    /// circuit-breaker history, the rate limiter and the learned pacing that mgx's own cmdlets
    /// go on using.
    /// </summary>
    [Fact]
    public void Disable_leaves_the_pipeline_the_factory_shares_with_mgxs_own_cmdlets()
    {
        using var scope = GraphSessionScope.Arm();
        using var sdkClient = new HttpClient(new OkHandler())
        { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        Enable(scope, sdkClient);

        var options = new ResilientGraphClientOptions
        {
            MaxRetryAttempts = 1,
            NoRateLimit = true,
            NoAdaptivePacing = true
        };
        var before = ResiliencePipelineFactory.GetOrCreate(options).Pipeline;

        try
        {
            DisableThroughTheCmdlet();

            Assert.Null(EnableMgxResilience.ResilientSdkClient);
            Assert.Null(EnableMgxResilience.ActiveHandler);
            Assert.Same(sdkClient, scope.Session.GraphHttpClient);
            Assert.Same(before, ResiliencePipelineFactory.GetOrCreate(options).Pipeline);
        }
        finally
        {
            ResiliencePipelineFactory.Reset();
        }
    }

    /// <summary>
    /// The transport GetClient last built a client on. MgxCmdletBase keeps it private, and it is
    /// read the way MeasureWrap reads a handler chain: what mgx sends through is the property
    /// under test, and no output above it tells one transport from another.
    /// </summary>
    private static HttpClient BorrowedTransport()
    {
        var field = typeof(MgxCmdletBase).GetField(
            "s_graphHttpClient", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(field != null, "MgxCmdletBase no longer keeps its transport in s_graphHttpClient");
        var transport = field!.GetValue(null) as HttpClient;
        Assert.True(transport != null, "GetClient did not build a transport");
        return transport!;
    }
}
