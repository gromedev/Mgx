using System.Diagnostics;

namespace Mgx.Engine.Http;

/// <summary>
/// Coarse workload class for a Graph request URI. Pacing state is partitioned by bucket so a
/// throttle on one service (e.g. Teams) does not slow an unrelated fan-out (e.g. Entra) in the
/// same process. Also decides the delta "sync from now" token form: OneDrive/SharePoint
/// resources take <c>token=latest</c>, everything else <c>$deltatoken=latest</c>.
/// </summary>
public enum WorkloadBucket
{
    /// <summary>OneDrive / SharePoint content plane (drives, sites, shares).</summary>
    Drive = 0,

    /// <summary>Entra directory objects (users, groups, servicePrincipals, ...).</summary>
    Directory = 1,

    /// <summary>Everything else (Exchange, Teams, Intune, reports, ...).</summary>
    Other = 2,

    /// <summary>
    /// $batch envelopes. Kept separate because GraphBatchClient governs batch throughput with
    /// its own item-level AIMD: a batch 429 landing in Other capped unrelated Exchange, Teams
    /// and Intune traffic on evidence that had nothing to do with them, while the workload the
    /// batch actually addressed went untouched. Measured budget isolation is per tenant and app
    /// identity, so mixing signals across workloads discards real information.
    /// </summary>
    Batch = 3
}

/// <summary>
/// Shared AIMD (additive-increase / multiplicative-decrease) pacing math and the workload
/// classifier. Extracted from GraphBatchClient so the request-level pacer
/// (AdaptiveRequestPacer) and batch item pacing use one set of rules. Public for the
/// classifier alone (the cmdlet layer picks the -Latest token form with it); the pacing
/// math stays internal.
/// </summary>
public static class AdaptivePacing
{
    internal const int WorkloadBucketCount = 4;

    /// <summary>Floor for any adapted rate. Halving without a floor would eventually reach
    /// zero, which disables pacing entirely - the opposite of what a throttled tenant needs.</summary>
    internal const int MinAdaptiveRate = 2;

    /// <summary>
    /// How long an adapted (reduced) rate persists after the last throttle before it no longer
    /// describes the tenant's current state. Also the quiet period after which the request
    /// pacer considers a workload "cold" and re-enters slow start.
    /// </summary>
    internal static readonly TimeSpan AdaptiveRecoveryWindow = TimeSpan.FromMinutes(5);

    /// <summary>Rate to fall back to after a throttle was observed.</summary>
    internal static int ReduceRate(int rate) => Math.Max(rate / 2, MinAdaptiveRate);

    /// <summary>Rate to climb to after a clean interval, capped at the configured rate.</summary>
    internal static int RecoverRate(int rate, int configuredRate) =>
        Math.Min(configuredRate, rate + Math.Max(1, configuredRate / 10));

    /// <summary>
    /// True when the persisted adapted rate is older than the recovery window, so the
    /// throttling that produced it no longer describes the tenant's current state.
    /// </summary>
    internal static bool AdaptedRateHasExpired(long lastThrottleTicks, long nowTicks) =>
        lastThrottleTicks > 0
        && nowTicks - lastThrottleTicks > (long)(AdaptiveRecoveryWindow.TotalSeconds * Stopwatch.Frequency);

    // Segment sets for Classify. Precedence: drive markers anywhere in the path win (a user's
    // OneDrive lives under /users/{id}/drive), then non-directory service markers anywhere
    // (a group's calendar lives under /groups/{id}/events but is Exchange-backed), then the
    // first segment decides directory membership. Other is the safe default: it only
    // determines pacing-state partitioning and the -Latest token form.
    private static readonly HashSet<string> DriveMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "drive", "drives", "sites", "shares"
    };

    private static readonly HashSet<string> NonDirectoryMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "messages", "mailfolders", "events", "calendar", "calendars", "calendarview",
        "contactfolders", "chats", "teams", "channels", "teamwork", "onenote", "planner",
        "todo", "photo", "photos", "presence", "insights", "onlinemeetings", "joinedteams"
    };

    private static readonly HashSet<string> DirectoryRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "users", "groups", "serviceprincipals", "applications", "directoryobjects",
        "devices", "directoryroles", "directoryroletemplates", "administrativeunits",
        "oauth2permissiongrants", "organization", "contacts", "directory", "me",
        "grouplifecyclepolicies", "subscribedskus", "domains"
    };

    /// <summary>
    /// Classify a request URI (absolute or relative, with or without query) into a workload
    /// bucket. Never throws; unparseable input lands in <see cref="WorkloadBucket.Other"/>.
    /// </summary>
    public static WorkloadBucket Classify(string? requestUri)
    {
        if (string.IsNullOrEmpty(requestUri)) return WorkloadBucket.Other;

        // Reduce to the path: strip scheme+authority if absolute, then the query/fragment.
        // The scheme check matters: on Unix, Uri.TryCreate parses a leading-slash relative
        // path as an absolute file:// URI and folds the query into AbsolutePath.
        var path = requestUri;
        if (Uri.TryCreate(requestUri, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp))
        {
            path = abs.AbsolutePath;
        }
        else
        {
            var cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;

        // Skip the API version segment when present ("v1.0" or "beta").
        if (segments.Length > 0 &&
            (segments[0].Equals("v1.0", StringComparison.OrdinalIgnoreCase)
             || segments[0].Equals("beta", StringComparison.OrdinalIgnoreCase)))
        {
            start = 1;
        }

        if (segments.Length <= start) return WorkloadBucket.Other;

        // $batch before anything else: the envelope's own URL says nothing about the workloads
        // inside it, and guessing from the first inner operation would be worse than admitting
        // the envelope is its own thing.
        for (var i = start; i < segments.Length; i++)
        {
            if (segments[i].Equals("$batch", StringComparison.OrdinalIgnoreCase))
                return WorkloadBucket.Batch;
        }

        for (var i = start; i < segments.Length; i++)
        {
            if (DriveMarkers.Contains(segments[i])) return WorkloadBucket.Drive;
        }

        for (var i = start; i < segments.Length; i++)
        {
            if (NonDirectoryMarkers.Contains(segments[i])) return WorkloadBucket.Other;
        }

        return DirectoryRoots.Contains(segments[start])
            ? WorkloadBucket.Directory
            : WorkloadBucket.Other;
    }
}
