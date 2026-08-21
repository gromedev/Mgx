using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Base;

/// <summary>
/// Lightweight base class for Mgx cmdlets that need Graph client access.
/// Provides auth and client lifecycle on top of <see cref="MgxCmdletCore"/>,
/// which supplies cancellation, disposal, and JSON-to-PSObject conversion.
/// Used by Invoke-MgxRequest and Invoke-MgxBatchRequest.
/// </summary>
public abstract class MgxCmdletBase : MgxCmdletCore
{
    private ResilientGraphClient? _client;

    private static readonly object s_initLock = new();
    private static HttpClient? s_graphHttpClient;
    private static bool s_ownsHttpClient; // false when using SDK fallback (don't dispose SDK's client)

    // Identity the cached client was built for
    private static volatile string? s_cachedAuthFingerprint;

    // WeakReference so a disconnected AuthContext (and the X509Certificate2 it holds) is not
    // kept alive by Mgx. A collected target can never be the current context.
    private static volatile WeakReference<object>? s_cachedAuthContextRef;

    // TotalTimeoutSeconds the cached client's HttpClient.Timeout was derived from.
    private static int s_cachedTotalTimeoutSeconds;

    internal static volatile string s_graphEndpoint = "https://graph.microsoft.com";
    internal static volatile ResilientGraphClientOptions s_clientOptions = ResilientGraphClientOptions.Default;

    /// <summary>
    /// Base URL for Graph API requests (e.g., "https://graph.microsoft.com/v1.0").
    /// Respects sovereign clouds via GraphSession environment.
    /// </summary>
    protected string GraphBaseUrl => $"{s_graphEndpoint}/v1.0";

    /// <summary>
    /// Get the resilient Graph client with auth-only HttpClient (no Kiota retry/redirect).
    /// Detects auth context changes and rebuilds the client when needed, so re-running
    /// Connect-MgGraph with a different tenant, application, or credential takes effect
    /// without restarting the session.
    /// </summary>
    protected ResilientGraphClient GetClient()
    {
        if (_client != null) return _client;

        var identity = GetCurrentAuthIdentity(WriteVerbose);
        if (string.IsNullOrEmpty(identity.Fingerprint))
        {
            // Microsoft.Graph.Authentication is a soft dependency (see mgx.psd1), so an empty
            // fingerprint has two distinct causes that need different advice. While it sat in
            // RequiredModules the SDK was guaranteed present and "run Connect-MgGraph" was
            // always the right answer; without it, that message sends someone to a cmdlet
            // that does not exist in their session.
            var (message, errorId) = IsGraphAuthLoaded()
                ? ("Not connected to Microsoft Graph. Run Connect-MgGraph first.",
                   "NotConnected")
                : ("Microsoft.Graph.Authentication is not loaded. Install it "
                   + "(Install-PSResource -Name Microsoft.Graph.Authentication) and run "
                   + "Connect-MgGraph, or supply your own transport via Enable-MgxResilience.",
                   "GraphAuthModuleNotLoaded");

            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(message),
                errorId,
                ErrorCategory.ConnectionError,
                null));
            return null!;
        }

        // Lock protects concurrent runspaces from racing on static client/endpoint init.
        // Capture locals inside lock to prevent TOCTOU race: another thread could enter
        // the lock and replace/dispose s_graphHttpClient between lock exit and usage.
        HttpClient httpClient;
        var identityChanged = false;
        var clientOptions = s_clientOptions;
        lock (s_initLock)
        {
            var previousFingerprint = s_cachedAuthFingerprint;
            var credentialChanged = !string.Equals(
                previousFingerprint, identity.Fingerprint, StringComparison.Ordinal);
            var contextReplaced = AuthContextInstanceChanged(identity.AuthContext);

            // A client borrowed from the SDK goes stale on its own terms, e.g. after Connect-MgGraph swaps
            // GraphSession.GraphHttpClient, and the instance we cached still carries the old auth.
            var sessionClient = s_ownsHttpClient ? null : TryGetSessionGraphHttpClient();
            var borrowedClientStale = sessionClient != null && s_graphHttpClient != null
                && !ReferenceEquals(s_graphHttpClient, sessionClient);

            // Set-MgxOption -TotalTimeoutSeconds only reaches HttpClient.Timeout through a
            // rebuild. The property is immutable once the first request has gone out.
            var timeoutStale = s_ownsHttpClient && s_graphHttpClient != null
                && s_cachedTotalTimeoutSeconds != clientOptions.TotalTimeoutSeconds;

            if (s_graphHttpClient == null || credentialChanged || contextReplaced
                || borrowedClientStale || timeoutStale)
            {
                if (timeoutStale)
                {
                    WriteVerbose($"TotalTimeoutSeconds changed ({s_cachedTotalTimeoutSeconds} -> "
                        + $"{clientOptions.TotalTimeoutSeconds}). Rebuilding the Mgx HTTP client.");
                }

                // previousFingerprint == null is the first build of the session, not a change.
                identityChanged = previousFingerprint != null && (credentialChanged || contextReplaced);
                if (identityChanged)
                {
                    WriteVerbose($"Graph identity changed ({Shorten(previousFingerprint)} -> "
                        + $"{Shorten(identity.Fingerprint)}). Rebuilding the Mgx HTTP client.");
                }

                // Reset-before-Build is intentional here (unlike TryPreInitHttpClient which
                // builds first then resets). GetClient() has a fallback path (SDK client), so
                // resetting circuit breaker state from the old tenant before attempting to build
                // is safe: if BuildCleanHttpClient fails, GetSdkHttpClientFallback provides a
                // working client. If both fail, ThrowTerminatingError is the correct response.
                // Only on a real credential change: a same-identity reconnect should keep its
                // warm rate limiter instead of earning a fresh burst allowance.
                if (credentialChanged) ResiliencePipelineFactory.Reset();
                // Schedule delayed disposal: in-flight ResilientGraphClient instances
                // may still hold a reference to the old client via their constructor
                ScheduleDelayedHttpClientDispose(s_graphHttpClient, s_ownsHttpClient);
                s_graphHttpClient = BuildCleanHttpClient(clientOptions.TotalTimeoutSeconds);
                if (s_graphHttpClient != null)
                {
                    s_ownsHttpClient = true;
                }
                else
                {
                    WriteWarning("Could not build auth-only HTTP client. Falling back to SDK client.");
                    s_graphHttpClient = GetSdkHttpClientFallback();
                    s_ownsHttpClient = false; // SDK owns this client; do NOT dispose
                }

                if (s_graphHttpClient == null)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        new InvalidOperationException(
                            "Failed to initialize Graph HTTP client. Ensure Connect-MgGraph has been called."),
                        "HttpClientInitFailed",
                        ErrorCategory.ConnectionError,
                        null));
                    return null!;
                }

                s_cachedAuthFingerprint = identity.Fingerprint;
                s_cachedAuthContextRef = identity.AuthContext is null
                    ? null
                    : new WeakReference<object>(identity.AuthContext);
                s_cachedTotalTimeoutSeconds = clientOptions.TotalTimeoutSeconds;
                s_graphEndpoint = GetGraphEndpoint(WriteWarning, WriteVerbose) ?? "https://graph.microsoft.com";
            }
            httpClient = s_graphHttpClient!;
        }

        // Outside s_initLock by design. Enable-MgxResilience takes StateLock and then s_initLock
        // (via TryPreInitHttpClient), so taking StateLock while holding s_initLock inverts the
        // lock order and can deadlock. Never acquire StateLock inside s_initLock.
        if (identityChanged)
            Cmdlets.Configuration.EnableMgxResilience.RefreshInjectedClient(WriteWarning, WriteVerbose);

        _client = new ResilientGraphClient(httpClient, clientOptions);
        _client.BodyReadTimeout = TimeSpan.FromSeconds(clientOptions.AttemptTimeoutSeconds);
        _client.VerboseWriter = msg => WriteVerbose(msg);
        _client.WarningWriter = msg => WriteWarning(msg);
        _client.DebugWriter = msg => WriteDebug(msg);
        _client.DebugEnabled = IsDebugRequested();
        return _client;
    }

    /// <summary>
    /// The Graph identity a client is built for. It's a comparable fingerprint plus the AuthContext
    /// instance it was derived from (null when only the Get-MgContext fallback could run).
    /// An empty fingerprint means "not connected".
    /// </summary>
    internal readonly record struct AuthIdentity(string Fingerprint, object? AuthContext);

    private static AuthIdentity GetCurrentAuthIdentity(Action<string>? verbose)
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            if (instance != null)
            {
                var authContext = instance.GetType().GetProperty("AuthContext")?.GetValue(instance);
                var fingerprint = BuildAuthFingerprint(authContext, GetGraphEndpointFrom(instance));
                // GraphSession is reachable, so its answer is authoritative - including
                // "no context", which means disconnected.
                return fingerprint.Length == 0 ? default : new AuthIdentity(fingerprint, authContext);
            }
        }
        catch (Exception ex)
        {
            verbose?.Invoke($"Failed to read GraphSession.AuthContext: {ex.Message}");
        }

        // Fallback for SDK internals drift using Get-MgContext
        try
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Get-MgContext");
            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0 || results[0] == null) return default;

            var context = UnwrapPSObject(results[0]);
            var fingerprint = BuildAuthFingerprint(context, null);
            return fingerprint.Length == 0 ? default : new AuthIdentity(fingerprint, context);
        }
        catch (Exception ex)
        {
            verbose?.Invoke($"Failed to get Graph auth context: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// True when the live AuthContext is a different object than the one the cached client was
    /// built from. Connect-MgGraph replaces the object, so this catches identity changes the
    /// value fingerprint cannot see (a rotated ClientSecret above all).
    /// </summary>
    private static bool AuthContextInstanceChanged(object? current)
    {
        var cachedRef = s_cachedAuthContextRef;
        if (current is null || cachedRef is null) return false;
        return !(cachedRef.TryGetTarget(out var cached) && ReferenceEquals(cached, current));
    }

    private static readonly string[] AuthFingerprintMembers =
    [
        "TenantId", "ClientId", "AuthType", "TokenCredentialType", "ContextScope",
        "Environment", "Account", "AppName", "ManagedIdentityId",
        "CertificateThumbprint", "CertificateSubjectName", "SendCertificateChain", "WamEnabled"
    ];

    /// <summary>
    /// Builds a comparable fingerprint of the effective Graph identity.
    /// </summary>
    internal static string BuildAuthFingerprint(object? authContext, string? graphEndpoint)
    {
        if (authContext == null) return string.Empty;
        if (Stringify(ReadAuthMember(authContext, "TenantId")).Length == 0) return string.Empty;

        var sb = new StringBuilder("mgx-auth-v1");
        foreach (var member in AuthFingerprintMembers)
            AppendField(sb, Stringify(ReadAuthMember(authContext, member)));

        // Certificate is an X509Certificate2, only its thumbprint identifies the credential.
        AppendField(sb, Stringify(ReadAuthMember(ReadAuthMember(authContext, "Certificate"), "Thumbprint")));

        // Scope order is not significant to Graph, so sort before hashing.
        AppendField(sb, ReadAuthMember(authContext, "Scopes") is IEnumerable scopes
            ? string.Join(",", scopes.Cast<object?>().Select(Stringify).OrderBy(s => s, StringComparer.Ordinal))
            : string.Empty);

        AppendField(sb, graphEndpoint ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private const char FieldSeparator = (char)0x1f;

    private static void AppendField(StringBuilder sb, string value) =>
        sb.Append(FieldSeparator).Append(value.Length).Append(':').Append(value);

    /// <summary>
    /// Reads a named member off an AuthContext-shaped object.
    /// </summary>
    internal static object? ReadAuthMember(object? source, string name)
    {
        if (source == null) return null;
        try
        {
            if (source is PSObject or IDictionary) return TryGetMember(source, name);
            return source.GetType().GetProperty(name)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static string Stringify(object? value) =>
        value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Shorten(string? fingerprint) =>
        string.IsNullOrEmpty(fingerprint) ? "none" : fingerprint[..Math.Min(8, fingerprint.Length)];

    internal static object? TryGetGraphSessionInstance()
    {
        var graphSessionType = FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession");
        return graphSessionType?.GetProperty("Instance",
            BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    /// <summary>
    /// Whether Microsoft.Graph.Authentication is present in the session. Because mgx does not
    /// declare it in RequiredModules, "absent" is a real state that has to be told apart from
    /// "present but disconnected" when reporting why a token could not be obtained.
    /// <para>
    /// Checks the type first, then Get-MgContext: the SDK's internal type layout has moved
    /// before, and the module can be loaded even when GraphSession is not where we look.
    /// FindType caches only successful lookups, so a module imported later is still seen.
    /// </para>
    /// </summary>
    internal static bool IsGraphAuthLoaded()
    {
        if (FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession") != null)
            return true;

        try
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Get-Command")
              .AddParameter("Name", "Get-MgContext")
              .AddParameter("ErrorAction", "SilentlyContinue");
            return ps.Invoke().Count > 0;
        }
        catch
        {
            // No runspace (hosted/test process) means no module either.
            return false;
        }
    }

    /// <summary>
    /// The HttpClient the Microsoft.Graph SDK is currently using, or null when it is not
    /// initialized. Used to detect that a borrowed SDK client has been replaced underneath us.
    /// </summary>
    internal static HttpClient? TryGetSessionGraphHttpClient()
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            return instance?.GetType().GetProperty("GraphHttpClient")?.GetValue(instance) as HttpClient;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetGraphEndpointFrom(object instance)
    {
        var env = instance.GetType().GetProperty("Environment")?.GetValue(instance);
        return env?.GetType().GetProperty("GraphEndpoint")?.GetValue(env)?.ToString();
    }

    internal static string? GetGraphEndpoint(Action<string>? warn, Action<string>? verbose)
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            return instance == null ? null : GetGraphEndpointFrom(instance);
        }
        catch (Exception ex)
        {
            warn?.Invoke("Failed to detect Graph endpoint. Falling back to graph.microsoft.com. "
                + "This may be incorrect for sovereign clouds (data sovereignty risk).");
            verbose?.Invoke($"Endpoint detection error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds an auth-only HttpClient using MSAL's AuthenticationHandler from the Graph SDK.
    /// Token lifecycle: MSAL's AuthenticationHandler refreshes tokens proactively
    /// (5 min before expiry). For operations spanning 2+ hours, token refresh is
    /// transparent as long as the Connect-MgGraph session remains valid and the
    /// refresh token has not been revoked.
    /// </summary>
    private HttpClient? BuildCleanHttpClient(int totalTimeoutSeconds) =>
        BuildCleanHttpClient(WriteWarning, WriteVerbose, totalTimeoutSeconds);

    private static HttpClient? BuildCleanHttpClient(
        Action<string> warn, Action<string> verbose, int totalTimeoutSeconds)
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            if (instance == null) return null;

            var authContext = instance.GetType().GetProperty("AuthContext")?.GetValue(instance);
            if (authContext == null) return null;

            // Save AzureADEndpoint before GetAuthenticationProviderAsync. A prior SDK call
            // (Connect-MgGraph or Invoke-MgGraphRequest) may have replaced GraphSession.Environment
            // with a new object that has empty AzureADEndpoint. Restore it before calling MSAL.
            var envObj = instance.GetType().GetProperty("Environment")?.GetValue(instance);
            var savedAadEndpoint = envObj?.GetType().GetProperty("AzureADEndpoint")?.GetValue(envObj)?.ToString();
            if (string.IsNullOrEmpty(savedAadEndpoint))
            {
                // AzureADEndpoint already corrupted (a prior Invoke-MgGraphRequest set it to empty).
                // Try to recover the base AAD host from AuthContext.Authority first.
                // AuthContext.Authority is computed as AzureADEndpoint + "/" + tenantId - so when
                // AzureADEndpoint was already empty at the time of computation, Authority is a
                // relative path like "/tenantId" and cannot be parsed as an absolute URI.
                var aadEndpoint = authContext.GetType().GetProperty("Authority")?.GetValue(authContext)?.ToString();
                var aadProp = envObj?.GetType().GetProperty("AzureADEndpoint");
                if (aadProp?.CanWrite == true)
                {
                    string? baseAuthority = null;
                    if (!string.IsNullOrEmpty(aadEndpoint) &&
                        System.Uri.TryCreate(aadEndpoint, UriKind.Absolute, out var authorityUri) &&
                        authorityUri.Scheme == "https")
                    {
                        // AuthContext.Authority is intact - extract scheme+host as the AAD base.
                        baseAuthority = $"{authorityUri.Scheme}://{authorityUri.Host}";
                    }
                    else
                    {
                        // Both AzureADEndpoint and AuthContext.Authority are corrupted.
                        // Infer the correct AAD base from GraphEndpoint (sovereign cloud mapping),
                        // falling back to global AAD for unknown or missing endpoints.
                        var graphEndpoint = envObj?.GetType().GetProperty("GraphEndpoint")?.GetValue(envObj)?.ToString();
                        baseAuthority = graphEndpoint switch
                        {
                            string e when !string.IsNullOrEmpty(e) && e.Contains("graph.microsoft.us")
                                => "https://login.microsoftonline.us",
                            string e when !string.IsNullOrEmpty(e) && e.Contains("microsoftgraph.chinacloudapi.cn")
                                => "https://login.chinacloudapi.cn",
                            _ => "https://login.microsoftonline.com"
                        };
                    }

                    aadProp.SetValue(envObj, baseAuthority);
                    verbose($"Restored AzureADEndpoint to {baseAuthority} (recovered from: {aadEndpoint ?? "null"})");
                }
            }

            var authHelpersType = FindType(
                "Microsoft.Graph.PowerShell.Authentication.Core.Utilities.AuthenticationHelpers");
            if (authHelpersType == null) return null;

            var getProviderMethod = authHelpersType.GetMethod("GetAuthenticationProviderAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (getProviderMethod == null) return null;

            var taskObj = getProviderMethod.Invoke(null, [authContext])!;
            ((Task)taskObj).GetAwaiter().GetResult();
            var authProvider = taskObj.GetType().GetProperty("Result")!.GetValue(taskObj);
            if (authProvider == null) return null;

            var authHandlerType = FindType(
                "Microsoft.Graph.PowerShell.Authentication.Handlers.AuthenticationHandler");
            if (authHandlerType == null) return null;

            var innerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = TransportDefaults.Decompression,
                PooledConnectionLifetime = TransportDefaults.PooledConnectionLifetime,
                MaxConnectionsPerServer = TransportDefaults.MaxConnectionsPerServer,
                EnableMultipleHttp2Connections = TransportDefaults.EnableMultipleHttp2Connections,
                ConnectTimeout = TransportDefaults.ConnectTimeout
            };

            DelegatingHandler? authHandler;
            try
            {
                authHandler = (DelegatingHandler)Activator.CreateInstance(
                    authHandlerType, authProvider, innerHandler)!;
            }
            catch
            {
                authHandler = (DelegatingHandler)Activator.CreateInstance(
                    authHandlerType, authProvider)!;
                authHandler.InnerHandler = innerHandler;
            }

            return new HttpClient(authHandler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                // Set HttpClient timeout as a safety net above Polly's TotalTimeoutSeconds.
                // Polly handles all normal timeout semantics. This outer timeout catches
                // edge cases where a connection bypasses Polly (pool exhaustion, DNS hang,
                // stale TLS). Set above Polly's TotalTimeoutSeconds so Polly fires first;
                // 60s of headroom prevents HttpClient from cancelling before Polly can react.
                Timeout = TimeSpan.FromSeconds(totalTimeoutSeconds + 60)
            };
        }
        catch (Exception ex)
        {
            warn($"Failed to build auth-only HTTP client: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pre-initializes Mgx's static HTTP client before any SDK probe runs.
    /// Called by Enable-MgxResilience before ForceInitializeAndGetClient as a
    /// performance optimization: builds the clean client while AzureADEndpoint
    /// is still intact, avoiding the save/restore overhead on subsequent calls.
    /// The root cause fix (RestoreAzureADEndpoint in ForceInitializeAndGetClient)
    /// handles the auth poisoning; this method is a belt-and-suspenders optimization.
    /// </summary>
    internal static void TryPreInitHttpClient(Action<string> warn, Action<string> verbose)
    {
        var identity = GetCurrentAuthIdentity(verbose);
        if (string.IsNullOrEmpty(identity.Fingerprint)) return;

        var options = s_clientOptions;

        // Quick early exit on the hot path (idempotent Enable calls).
        // Volatile.Read ensures ARM64 memory visibility - without it, non-volatile statics
        // read outside a lock have no acquire barrier and may return stale values.
        if (Volatile.Read(ref s_graphHttpClient) != null && !ClientIsStale(identity, options)) return;

        lock (s_initLock)
        {
            if (s_graphHttpClient != null && !ClientIsStale(identity, options)) return;

            // Build first. Only dispose/reset after we have a confirmed replacement.
            // If BuildCleanHttpClient fails, the existing s_graphHttpClient must remain valid
            // and callers fall back via GetClient() on first use.
            var client = BuildCleanHttpClient(warn, verbose, options.TotalTimeoutSeconds);
            if (client == null) return; // BuildCleanHttpClient already warned with ex.Message

            ResiliencePipelineFactory.Reset();
            ScheduleDelayedHttpClientDispose(s_graphHttpClient, s_ownsHttpClient);
            s_graphHttpClient = client;
            s_ownsHttpClient = true;
            s_cachedAuthFingerprint = identity.Fingerprint;
            s_cachedAuthContextRef = identity.AuthContext is null
                ? null
                : new WeakReference<object>(identity.AuthContext);
            s_cachedTotalTimeoutSeconds = options.TotalTimeoutSeconds;
            s_graphEndpoint = GetGraphEndpoint(warn, verbose) ?? "https://graph.microsoft.com";
        }
    }

    /// <summary>
    /// True when the cached client no longer matches the given identity (by either signal) or
    /// the timeout it was built with. Callers must hold s_initLock, or accept a benign rebuild.
    /// </summary>
    private static bool ClientIsStale(AuthIdentity identity, ResilientGraphClientOptions options) =>
        !string.Equals(s_cachedAuthFingerprint, identity.Fingerprint, StringComparison.Ordinal)
        || AuthContextInstanceChanged(identity.AuthContext)
        || s_cachedTotalTimeoutSeconds != options.TotalTimeoutSeconds;

    private HttpClient? GetSdkHttpClientFallback()
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            if (instance == null) return null;

            var httpClient = instance.GetType().GetProperty("GraphHttpClient")
                ?.GetValue(instance) as HttpClient;
            if (httpClient != null) return httpClient;

            // Use detected endpoint instead of hardcoded graph.microsoft.com
            // (supports sovereign clouds: GCC-High, DoD, China)
            var endpoint = GetGraphEndpoint(WriteWarning, WriteVerbose) ?? "https://graph.microsoft.com";

            // Save AzureADEndpoint before probe (same issue as ForceInitializeAndGetClient:
            // Invoke-MgGraphRequest replaces GraphSession.Environment with a new object
            // that has empty AzureADEndpoint, breaking GetAuthenticationProviderAsync).
            var envObj = instance.GetType().GetProperty("Environment")?.GetValue(instance);
            var savedAadEndpoint = envObj?.GetType().GetProperty("AzureADEndpoint")?.GetValue(envObj)?.ToString();

            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Invoke-MgGraphRequest");
            ps.AddParameter("Method", "GET");
            ps.AddParameter("Uri", $"{endpoint}/v1.0/organization?$top=1&$select=id");
            ps.AddParameter("ErrorAction", "Stop");
            ps.AddParameter("WarningAction", "SilentlyContinue"); // suppress incidental probe warnings
            ps.AddCommand("Out-Null"); // suppress output from flowing to caller's pipeline
            ps.Invoke();

            // Restore AzureADEndpoint on the NEW Environment object the probe created.
            RestoreAzureADEndpoint(instance, savedAadEndpoint, WriteVerbose);

            return instance.GetType().GetProperty("GraphHttpClient")
                ?.GetValue(instance) as HttpClient;
        }
        catch (Exception ex)
        {
            WriteWarning($"Failed to get Graph HttpClient: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Restores AzureADEndpoint on the GraphSession Environment if the SDK probe cleared it.
    /// The probe replaces GraphSession.Environment with a new object that has empty AzureADEndpoint,
    /// so we must re-read the property after the probe to patch the new object.
    /// </summary>
    internal static void RestoreAzureADEndpoint(object graphSessionInstance, string? savedAadEndpoint, Action<string>? verbose)
    {
        if (string.IsNullOrEmpty(savedAadEndpoint)) return;

        var envObj = graphSessionInstance.GetType().GetProperty("Environment")?.GetValue(graphSessionInstance);
        var aadProp = envObj?.GetType().GetProperty("AzureADEndpoint");
        if (aadProp?.CanWrite != true) return;

        var current = aadProp.GetValue(envObj)?.ToString();
        if (string.IsNullOrEmpty(current))
        {
            aadProp.SetValue(envObj, savedAadEndpoint);
            verbose?.Invoke($"Restored AzureADEndpoint after SDK fallback probe: {savedAadEndpoint}");
        }
    }

    // Cache for FindType: avoids scanning all loaded assemblies on every call.
    // ConcurrentDictionary is safe for concurrent runspaces.
    // Only non-null results are cached: assemblies load lazily in PowerShell,
    // so a miss now may succeed after the user imports additional modules.
    private static readonly ConcurrentDictionary<string, Type> s_typeCache = new();

    static MgxCmdletBase()
    {
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    // A cached Type carries the identity of the assembly that defined it. Re-importing
    // Microsoft.Graph.Authentication - a different version, or into a fresh load context -
    // produces a second GraphSession type whose Instance is a different singleton, so a stale
    // entry would silently point Mgx at a session nobody else is using. Assemblies only ever
    // appear, never disappear, so any load is the signal to re-resolve. Clearing an almost
    // always tiny dictionary is cheaper than validating entries on the hot path.
    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args) => s_typeCache.Clear();

    /// <summary>
    /// Detaches the assembly-load hook. Called on module removal so the handler does not
    /// root this type after the module is gone.
    /// </summary>
    internal static void DetachAssemblyLoadHandler() =>
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

    internal static Type? FindType(string fullName)
    {
        if (s_typeCache.TryGetValue(fullName, out var cached))
            return cached;

        var found = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .FirstOrDefault(t => t.FullName == fullName);

        if (found != null)
            s_typeCache.TryAdd(fullName, found);

        return found;
    }

    public static void ResetHttpClient()
    {
        lock (s_initLock)
        {
            ScheduleDelayedHttpClientDispose(s_graphHttpClient, s_ownsHttpClient);
            s_graphHttpClient = null;
            s_ownsHttpClient = false;
            s_cachedAuthFingerprint = null;
            s_cachedAuthContextRef = null;
            ResiliencePipelineFactory.Reset();
        }
    }

    /// <summary>
    /// Disposes an HttpClient after a delay. In-flight ResilientGraphClient instances
    /// may still hold a reference to the old client, so we wait for the total timeout
    /// window to ensure all in-flight requests complete before disposing.
    /// Same pattern as ResiliencePipelineFactory.ScheduleDelayedDispose for rate limiters.
    /// </summary>
    private static void ScheduleDelayedHttpClientDispose(HttpClient? client, bool owned)
    {
        // Ownership is passed in rather than read off the static. Callers replace the static
        // right after this call, so reading it here would silently follow whichever edit lands
        // first - and disposing a borrowed SDK client kills Invoke-MgGraphRequest session-wide.
        if (client == null || !owned) return;
        var delaySeconds = s_clientOptions.TotalTimeoutSeconds;
        _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
        {
            try { client.Dispose(); } catch { /* best-effort cleanup */ }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Whether the active transport is the mgx-owned clean client (AllowAutoRedirect off)
    /// rather than the borrowed SDK client. The content path requires ownership: the SDK
    /// client ships a RedirectHandler that auto-follows a content 302 to a host mgx never
    /// validated, so Get-MgxContent fails closed when this is false.
    /// </summary>
    protected static bool TransportIsOwned => s_ownsHttpClient;

    /// <summary>
    /// Drain buffered verbose messages from the resilience pipeline.
    /// Must be called on the pipeline thread (after .GetAwaiter().GetResult() returns).
    /// OnRetry fires on thread pool threads after Task.Delay, so WriteVerbose cannot
    /// be called directly from OnRetry. Messages are buffered and drained here.
    /// </summary>
    protected void DrainClientMessages()
    {
        _client?.DrainVerboseMessages();
        _client?.DrainWarningMessages();
        _client?.DrainDebugMessages();
    }

    /// <summary>
    /// Whether the caller asked for the HTTP trace, via -Debug on this invocation or a
    /// $DebugPreference that would display the messages anyway.
    /// </summary>
    protected bool IsDebugRequested()
    {
        if (MyInvocation.BoundParameters.TryGetValue("Debug", out var debug)
            && debug is SwitchParameter { IsPresent: true })
        {
            return true;
        }

        return GetVariableValue("DebugPreference") is ActionPreference preference
            && preference != ActionPreference.SilentlyContinue
            && preference != ActionPreference.Ignore;
    }

    internal static void SetClientOptions(ResilientGraphClientOptions options)
    {
        s_clientOptions = options ?? ResilientGraphClientOptions.Default;
    }

    /// <summary>
    /// Remove leftover "{outputPath}.{guid}.tmp" files. Called only when no resume is pending,
    /// where every such file is an orphan by definition.
    /// </summary>
    /// <summary>
    /// True when nothing else holds the file. FileShare.None is honoured between .NET processes
    /// on both Windows and Unix, so a writer that has it open makes this fail rather than let a
    /// sweep take a file out from under it.
    /// </summary>
    private static bool CanTakeExclusively(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected void DeleteStaleTemps(string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var stale in Directory.EnumerateFiles(dir, Path.GetFileName(outputPath) + ".*.tmp").ToList())
            {
                // "Orphan" is an assumption about a file this run did not create, and a second
                // export running against the same output right now owns a file matching the same
                // glob. Windows refuses to delete a file someone holds open, so it declined by
                // accident; Unix does not, and the other run went on writing into an unlinked
                // inode and lost everything it had fetched. Ask for the file exclusively first -
                // if that fails, someone is using it and it is not an orphan.
                if (!CanTakeExclusively(stale))
                {
                    WriteVerbose($"Left '{Path.GetFileName(stale)}' alone: another run is writing to it.");
                    continue;
                }
                try
                {
                    File.Delete(stale);
                    WriteVerbose($"Deleted an orphaned temp file from an earlier interrupted run: {Path.GetFileName(stale)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteWarning($"Could not delete orphaned temp file '{stale}': {ex.Message}. Delete it manually.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a sweep failure must never stop the run.
        }
    }

    /// <summary>
    /// The temp a checkpoint names, or null when it cannot be used. A checkpoint is untrusted
    /// input once it is on disk, so the recorded name must be one a run could actually have
    /// written - "{output}.{32-hex}.tmp" - and must not be the output itself. Anything else
    /// (the checkpoint file, the delta state, a crafted path) would be copied into the output
    /// as data and then deleted as the spent temp. The file must also be at least as long as
    /// the checkpoint promised, since a shorter one means the items it counted are not all
    /// there.
    /// </summary>
    private static string? ResolveNamedTemp(string outputPath, string tempFileName, long dataLength)
    {
        if (dataLength <= 0) return null;
        var dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        if (!string.Equals(tempFileName, Path.GetFileName(tempFileName), StringComparison.Ordinal))
            return null;
        if (!IsRunTempName(Path.GetFileName(outputPath), tempFileName)) return null;
        var tempPath = Path.Combine(dir, tempFileName);
        if (string.Equals(Path.GetFullPath(tempPath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(tempPath)) return null;
        if (new FileInfo(tempPath).Length < dataLength) return null;
        return tempPath;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a name a fresh run gives its temp:
    /// the output's own name, a dot, 32 lowercase hex digits (Guid "N"), and ".tmp".
    /// </summary>
    private static bool IsRunTempName(string outputFileName, string candidate)
    {
        var prefix = outputFileName + ".";
        const string suffix = ".tmp";
        if (candidate.Length != prefix.Length + 32 + suffix.Length) return false;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!candidate.EndsWith(suffix, StringComparison.Ordinal)) return false;
        for (var i = prefix.Length; i < prefix.Length + 32; i++)
        {
            if (candidate[i] is (>= '0' and <= '9') or (>= 'a' and <= 'f')) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Replace the output with the first <paramref name="dataLength"/> bytes of a named temp,
    /// then remove the temp. A fresh run that finishes moves its temp over whatever the output
    /// held, so recovering an unfinished one has to reach that same file; appending instead
    /// would leave the previous run's rows - already consumed - in front of this one's. Unlike
    /// the glob-and-newest form, this takes exactly the file the checkpoint recorded, so a
    /// leftover from an unrelated run cannot be merged in. Bytes rather than lines: the length
    /// was taken from the writer's own position, so it cannot disagree with itself about line
    /// endings or a torn final line. Returns false when the temp is absent or shorter than the
    /// checkpoint promised, which means the caller must not resume past the items it counted.
    /// </summary>
    protected static bool TryPromoteNamedTemp(string outputPath, string tempFileName, long dataLength)
    {
        try
        {
            var tempPath = ResolveNamedTemp(outputPath, tempFileName, dataLength);
            if (tempPath == null) return false;

            // Staged like the other forms, so the destination is replaced in one Move rather
            // than truncated and refilled in place.
            var adoptPath = outputPath + ".adopt";
            using (var writer = new FileStream(adoptPath, FileMode.Create, FileAccess.Write))
            {
                using var temp = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                var buffer = new byte[81920];
                long remaining = dataLength;
                while (remaining > 0)
                {
                    var read = temp.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    writer.Write(buffer, 0, read);
                    remaining -= read;
                }
                if (remaining > 0)
                {
                    writer.Dispose();
                    File.Delete(adoptPath);
                    return false;
                }
            }
            File.Move(adoptPath, outputPath, overwrite: true);
            try { File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cut the output back to the length a checkpoint recorded for it, dropping anything the
    /// interrupted run wrote after its last save. Those items are re-fetched, so dropping them
    /// is what keeps a resume from duplicating them. Returns false when the output is shorter
    /// than recorded, which means it is no longer the file the checkpoint describes.
    /// </summary>
    protected static bool TryTrimOutputToCheckpoint(string outputPath, long dataLength)
    {
        try
        {
            // A checkpoint is untrusted input once it is on disk, and SetLength rejects a
            // negative length with an ArgumentOutOfRangeException the catch below does not
            // cover - so a hand-edited length escaped as a terminating error naming neither the
            // checkpoint nor the file, and left itself on disk to fail the same way next run.
            // ResolveNamedTemp already guards its own length; this is the same guard.
            if (dataLength < 0) return false;
            if (!File.Exists(outputPath)) return false;
            var actual = new FileInfo(outputPath).Length;
            if (actual < dataLength) return false;
            if (actual == dataLength) return true;
            using var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Write);
            fs.SetLength(dataLength);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recovers a fresh-run JSONL job that was interrupted before its temp file was promoted,
    /// from a checkpoint that predates the recorded temp name and length. With only a line
    /// count and the newest matching temp to go on, this is safe solely when no output exists:
    /// everything the run wrote is then in its temp, and creating the output from it is what a
    /// finishing run's own promotion would have done. Against an existing output there is no
    /// way to tell whose items the temp holds, so the caller must re-enumerate instead.
    /// Copies exactly <paramref name="itemCount"/> lines - content beyond the last flush may
    /// be absent or torn - then removes the temp. Returns false when nothing usable exists,
    /// leaving the caller to the stale-checkpoint path.
    /// </summary>
    protected static bool TryAdoptOrphanedTemp(string outputPath, long itemCount)
    {
        try
        {
            if (itemCount <= 0) return false;
            if (File.Exists(outputPath)) return false;
            var dir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var temp = Directory.EnumerateFiles(dir, Path.GetFileName(outputPath) + ".*.tmp")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (temp == null) return false;

            // Staged so the output appears in one Move. A merge that dies halfway leaves only
            // the staging file behind, and the caller keeps its checkpoint - the safe direction.
            long copied = 0;
            var adoptPath = outputPath + ".adopt";
            using (var writer = new StreamWriter(adoptPath, append: false))
            {
                using var reader = new StreamReader(temp.FullName);
                string? line;
                while (copied < itemCount && (line = reader.ReadLine()) != null)
                {
                    writer.WriteLine(line);
                    copied++;
                }
            }
            if (copied < itemCount)
            {
                // Temp holds less than the checkpoint promises - unusable.
                File.Delete(adoptPath);
                return false;
            }
            File.Move(adoptPath, outputPath, overwrite: true);
            temp.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    #region Shared URL and header builders

    protected static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.StartsWith('/') ? path : $"/{path}";
    }

    protected static Dictionary<string, string>? BuildRequestHeaders(
        string? consistencyLevel, System.Collections.Hashtable? extraHeaders)
    {
        Dictionary<string, string>? headers = null;

        // Apply extraHeaders first so dedicated parameters can override
        if (extraHeaders != null)
        {
            headers = new Dictionary<string, string>();
            foreach (var key in extraHeaders.Keys)
                headers[key.ToString()!] = extraHeaders[key]?.ToString() ?? string.Empty;
        }

        // Dedicated -ConsistencyLevel parameter always wins over -Headers key
        if (!string.IsNullOrEmpty(consistencyLevel))
        {
            headers ??= new Dictionary<string, string>();
            headers["ConsistencyLevel"] = consistencyLevel;
        }

        return headers;
    }

    protected record ODataListParams(
        bool NoPageSize,
        int Top,
        int PageSize,
        string? Filter,
        string[]? Property,
        string[]? Sort,
        string? Search,
        int Skip,
        string[]? ExpandProperty,
        bool IncludeCount = false);

    protected static string BuildListUrl(string versionedBaseUrl, string relativeUri, ODataListParams p)
    {
        var baseUrl = $"{versionedBaseUrl}{NormalizePath(relativeUri)}";
        var queryParams = new List<string>();

        if (!p.NoPageSize)
        {
            var effectiveTop = p.Top > 0 ? Math.Min(p.Top, p.PageSize) : p.PageSize;
            queryParams.Add($"$top={effectiveTop}");
        }

        if (!string.IsNullOrEmpty(p.Filter))
            queryParams.Add($"$filter={Uri.EscapeDataString(p.Filter)}");

        if (p.Property is { Length: > 0 })
            queryParams.Add($"$select={Uri.EscapeDataString(string.Join(",", p.Property))}");

        if (p.Sort is { Length: > 0 })
            queryParams.Add($"$orderby={Uri.EscapeDataString(string.Join(",", p.Sort))}");

        if (!string.IsNullOrEmpty(p.Search))
        {
            // Graph API requires $search values wrapped in double quotes: $search="displayName:John"
            var searchValue = p.Search;
            if (!searchValue.StartsWith('"') || !searchValue.EndsWith('"'))
                searchValue = $"\"{searchValue}\"";
            queryParams.Add($"$search={Uri.EscapeDataString(searchValue)}");
        }

        if (p.Skip > 0)
            queryParams.Add($"$skip={p.Skip}");

        if (p.ExpandProperty is { Length: > 0 })
            queryParams.Add($"$expand={Uri.EscapeDataString(string.Join(",", p.ExpandProperty))}");

        // $count=true: required explicitly via -CountVariable, or implicitly when $search is used
        // (Graph advanced query capabilities require $count=true alongside $search)
        if (p.IncludeCount || !string.IsNullOrEmpty(p.Search))
            queryParams.Add("$count=true");

        if (queryParams.Count == 0)
            return baseUrl;

        // If URI already contains query parameters, append with & instead of ?
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", queryParams)}";
    }

    #endregion

    protected string CircuitBreakerMessage =>
        $"Circuit breaker tripped: too many failures caused Mgx to temporarily stop requests. " +
        $"Wait {s_clientOptions.CircuitBreakerDurationSeconds}s or run Get-MgxTelemetry for details. " +
        $"Tune with Set-MgxOption -CircuitBreakerFailureRatio / -CircuitBreakerMinThroughput.";

    /// <summary>
    /// Codes measured to mean the PATH was fine and the OBJECT was not there, so a beta hint
    /// over them sends the caller to re-run a request that fails there too. Only codes with
    /// demonstrated semantics belong here. Request_ResourceNotFound does NOT qualify: Graph
    /// returns it both for a missing directory object and for a beta-only segment on v1.0
    /// (measured against /users/{id} and /users/{id}/profile), so it cannot be told apart and
    /// the hedged hint stays. itemNotFound is the drive service reporting an absent item - an
    /// unknown drive segment is a 400, not a 404, so the ambiguity does not arise there.
    /// </summary>
    private static readonly HashSet<string> ObjectMissingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "itemNotFound",
    };

    /// <summary>True when the exception is a Graph 404 that names a missing object.</summary>
    protected static bool IsObjectMissing(Exception ex) =>
        ex is GraphServiceException { StatusCode: HttpStatusCode.NotFound } g
        && g.ErrorCode != null
        && ObjectMissingCodes.Contains(g.ErrorCode);

    protected void WriteBetaHintIfApplicable(HttpStatusCode statusCode, string apiVersion,
        string? errorCode = null)
    {
        if (statusCode != HttpStatusCode.NotFound ||
            !string.Equals(apiVersion, "v1.0", StringComparison.OrdinalIgnoreCase))
            return;

        // A missing user, group or drive item is not an absent endpoint.
        if (errorCode != null && ObjectMissingCodes.Contains(errorCode))
            return;

        WriteWarning("This endpoint may only be available in beta. Retry with -ApiVersion beta.");
    }

    protected static ErrorCategory MapStatusToCategory(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => ErrorCategory.ObjectNotFound,
        HttpStatusCode.Unauthorized => ErrorCategory.AuthenticationError,
        HttpStatusCode.Forbidden => ErrorCategory.PermissionDenied,
        HttpStatusCode.BadRequest => ErrorCategory.InvalidArgument,
        HttpStatusCode.Conflict => ErrorCategory.ResourceExists,
        (HttpStatusCode)429 => ErrorCategory.LimitsExceeded,
        _ => ErrorCategory.NotSpecified
    };

    /// <summary>
    /// Handles the three common terminal exception types (GraphServiceException,
    /// BrokenCircuitException, HttpRequestException) that appear in every cmdlet's
    /// catch cascade. Drains buffered messages, writes beta hint if applicable,
    /// and writes the error record.
    /// Returns true if the exception was handled; false if unrecognized.
    /// </summary>
    protected bool WriteGraphError(Exception ex, object? target, string? apiVersion = null)
    {
        DrainClientMessages();

        switch (ex)
        {
            case GraphServiceException gex:
                if (apiVersion != null)
                    WriteBetaHintIfApplicable(gex.StatusCode, apiVersion, gex.ErrorCode);
                WriteError(new ErrorRecord(gex, gex.ErrorCode ?? "GraphError",
                    MapStatusToCategory(gex.StatusCode), target));
                return true;

            case BrokenCircuitException bcex:
                WriteError(new ErrorRecord(
                    new InvalidOperationException(CircuitBreakerMessage, bcex),
                    "CircuitBroken", ErrorCategory.ResourceUnavailable, target));
                return true;

            case HttpRequestException hex:
                WriteError(new ErrorRecord(hex, "HttpError",
                    ErrorCategory.ConnectionError, target));
                return true;

            default:
                return false;
        }
    }

    // Count discrepancy detection thresholds.
    // Not user-configurable (YAGNI). Change these constants if defaults prove problematic.
    // Undercount: 10% tolerance prevents noise from eventual consistency lag;
    // 100-item floor avoids false alarms on small collections.
    // Overcount: much tighter (0.5%, 50-item floor) - the observed failure mode is a
    // duplicated page from a service-side skiptoken overlap (~one $top of extras),
    // which a symmetric 10% tolerance would never catch at scale.
    protected const double CountDiscrepancyThreshold = 0.9;
    protected const long CountDiscrepancyMinItems = 100;
    protected const double CountOvershootThreshold = 0.005;
    protected const long CountOvershootMinItems = 50;

    protected void WriteCountDiscrepancyWarning(
        string resource, long reportedCount, long actualCount, string? filter)
    {
        if (reportedCount < CountDiscrepancyMinItems) return;

        if (actualCount > reportedCount)
        {
            var overshoot = actualCount - reportedCount;
            if (overshoot <= Math.Max(CountOvershootMinItems, (long)(reportedCount * CountOvershootThreshold)))
                return;
            WriteWarning(
                $"[{resource}] Graph returned {actualCount} items but reported a count of {reportedCount} "
                + $"({overshoot} extra). This can indicate a duplicated page during pagination "
                + "(observed as a transient service-side skiptoken overlap). If the output feeds a "
                + "downstream system, deduplicate on 'id'.");
            return;
        }

        if (actualCount >= (long)(reportedCount * CountDiscrepancyThreshold)) return;

        var pct = reportedCount > 0 ? (int)((1.0 - (double)actualCount / reportedCount) * 100) : 0;
        var cause = !string.IsNullOrEmpty(filter)
            ? "This may indicate insufficient permissions for the applied $filter. "
              + "Verify the required scopes at https://learn.microsoft.com/graph/permissions-reference"
            : "Items may have been removed during enumeration, "
              + "or eventual consistency lag produced a stale count";
        WriteWarning(
            $"[{resource}] Graph reported {reportedCount} items but only {actualCount} "
            + $"were returned ({pct}% shortfall). {cause}.");
    }

    /// <summary>
    /// Release the Graph client. Invoked by <see cref="MgxCmdletCore.Dispose"/> under
    /// the Interlocked guard, so this runs exactly once even when StopProcessing
    /// (pipeline-stopping thread) races EndProcessing (pipeline thread).
    /// </summary>
    protected override void DisposeCore()
    {
        _client?.Dispose();
    }
}
