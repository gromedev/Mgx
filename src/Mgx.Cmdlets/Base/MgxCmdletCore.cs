using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mgx.Cmdlets.Base;

/// <summary>
/// Protocol-neutral base for Mgx cmdlets. Owns cancellation, disposal, and JSON-to-Hashtable
/// conversion — everything that is not tied to a specific transport.
/// <para>
/// Graph cmdlets derive from <see cref="MgxCmdletBase"/>, which adds the Graph HTTP client and
/// auth on top of this. Keeping the two apart means a cmdlet needing only the lifecycle and
/// conversion helpers does not also inherit the static HttpClient state or the
/// Connect-MgGraph requirement.
/// </para>
/// </summary>
public abstract class MgxCmdletCore : PSCmdlet, IDisposable
{
    private CancellationTokenSource _cts = new();
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked for thread safety)

    // Regex gate for DateTime parsing: requires YYYY-MM-DDT prefix.
    // Prevents false positives on version strings, GUIDs, numeric IDs.
    private static readonly Regex Iso8601Pattern = new(
        @"^\d{4}-\d{2}-\d{2}[T ]", RegexOptions.Compiled);

    protected CancellationToken CancellationToken => _cts.Token;

    #region Lifecycle

    protected override void StopProcessing()
    {
        _cts.Cancel();
        Dispose();
    }

    protected override void EndProcessing()
    {
        Dispose();
    }

    /// <summary>
    /// Subclass hook for releasing transport-specific resources (the Graph HTTP client).
    /// Called exactly once, inside the same Interlocked guard that protects <see cref="Dispose"/>.
    /// </summary>
    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        // Thread-safe: StopProcessing (pipeline-stopping thread) and EndProcessing (pipeline thread)
        // can race. Interlocked ensures only one thread enters the dispose body.
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _cts.Cancel();
            _cts.Dispose();
            DisposeCore();
        }
        GC.SuppressFinalize(this);
    }

    #endregion

    #region JSON conversion

    /// <summary>
    /// Convert a JsonElement to a case-insensitive Hashtable with all properties preserved.
    /// </summary>
    protected internal static Hashtable JsonToHashtable(JsonElement element)
    {
        // OrdinalIgnoreCase matches PowerShell's @{} literal, so member access stays
        // case-insensitive ($user.DisplayName resolves the camelCase 'displayName' key).
        var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);

        // Non-Object elements (string, number, etc.) must wrap value in a property
        if (element.ValueKind != JsonValueKind.Object)
        {
            ht["Value"] = ConvertJsonValue(element);
            return ht;
        }

        foreach (var prop in element.EnumerateObject())
        {
            // Strip @odata.* transport metadata (nextLink, context, count, etag), but keep
            // @odata.type verbatim: it round-trips on write and drives polymorphic handling.
            //
            // @odata.etag is stripped deliberately. It changes on every write, so preserving it
            // makes two reads of an unchanged entity compare unequal - which surfaces as phantom
            // drift in state-comparison consumers such as Microsoft365DSC. Callers that need the
            // If-Match tag can read it from the raw payload (-Raw | ConvertFrom-Json).
            if (prop.Name.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase)
                && !prop.Name.Equals("@odata.type", StringComparison.OrdinalIgnoreCase))
                continue;

            // Indexer, not Add: keys differing only by case would throw with Add
            ht[prop.Name] = ConvertJsonValue(prop.Value);
        }

        return ht;
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (str != null && Iso8601Pattern.IsMatch(str) &&
                DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dto))
                return dto.UtcDateTime;
            return str;
        }

        return element.ValueKind switch
        {
            // The (object) cast is required: without it the conditional unifies to double,
            // widening every integer and losing precision beyond 2^53.
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Object ? (object?)JsonToHashtable(item) : ConvertJsonValue(item))
                .ToArray(),
            JsonValueKind.Object => JsonToHashtable(element),
            _ => element.GetRawText()
        };
    }

    #endregion

    #region Pipeline input helpers

    /// <summary>
    /// Unwrap a PSObject to the .NET value underneath (string, Hashtable, ...). A
    /// PSCustomObject is returned as its PSObject: its members live on the PSObject,
    /// and its BaseObject is an empty PSCustomObject marker that carries nothing.
    /// </summary>
    protected internal static object UnwrapPSObject(object input) =>
        input is PSObject pso && pso.BaseObject is not PSObject and not PSCustomObject
            ? pso.BaseObject
            : input;

    /// <summary>
    /// Read a named member from pipeline input, whether it is a Hashtable, a
    /// PSObject-wrapped dictionary, or a PSCustomObject.
    /// </summary>
    protected internal static object? TryGetMember(object? input, string name)
    {
        if (input is PSObject wrapper && wrapper.BaseObject is IDictionary baseDict)
            return baseDict[name];
        if (input is IDictionary dict)
            return dict[name];
        if (input is PSObject pso)
            return pso.Properties[name]?.Value;
        return null;
    }

    #endregion
}
