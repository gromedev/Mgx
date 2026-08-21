namespace Mgx.Engine.Pagination;

/// <summary>
/// Validates @odata.nextLink URLs to prevent SSRF attacks.
/// A poisoned nextLink (from a crafted Graph response or tampered checkpoint)
/// pointing to an attacker's server would leak the bearer token.
/// </summary>
public static class NextLinkValidator
{
    /// <summary>
    /// Returns the nextLink unchanged if it passes all validation checks,
    /// or null if it should be rejected (stopping pagination).
    /// </summary>
    public static string? Validate(string? nextLink, Uri? expectedHost, string? expectedPathPrefix = null)
    {
        if (nextLink == null || expectedHost == null) return null;

        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri))
            return null;

        // Reject non-HTTPS: prevents scheme-downgrade attacks that would
        // send the bearer token over plaintext HTTP
        if (!string.Equals(nextUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return null;

        // Compare Authority (host + port) to catch port-based redirects
        if (!string.Equals(nextUri.Authority, expectedHost.Authority, StringComparison.OrdinalIgnoreCase))
            return null;

        // Optional: validate path prefix to prevent same-host cross-resource redirection.
        // A tampered checkpoint could redirect /users pagination to /me/messages on the
        // same host, exfiltrating different data with the user's token.
        if (expectedPathPrefix != null &&
            !nextUri.AbsolutePath.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return nextLink;
    }

    /// <summary>
    /// Validates a nextLink the service actually sent, throwing if it is refused.
    /// Use this mid-pagination. A null from <see cref="Validate"/> is ambiguous there: it means
    /// both "there was no link" and "the link was refused", and treating the second like the
    /// first ends the loop quietly and hands back a partial collection that looks complete.
    /// Returns null only when there genuinely was no link, i.e. the end of the collection.
    /// </summary>
    public static string? ValidateOrThrow(string? nextLink, Uri? expectedHost, string? expectedPathPrefix = null)
    {
        if (nextLink == null) return null;

        var validated = Validate(nextLink, expectedHost, expectedPathPrefix);
        if (validated == null)
            throw new InvalidOperationException(
                $"Pagination stopped: the service returned an @odata.nextLink that failed validation " +
                $"({Describe(nextLink)}). Expected an https link on '{expectedHost?.Authority}'. " +
                "Following it could send the access token to another host, and ignoring it would " +
                "return part of the collection as though it were all of it.");

        return validated;
    }

    /// <summary>Scheme, authority and path only - the query can carry anything on a crafted link.</summary>
    private static string Describe(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u)
            ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath}"
            : "unparseable";
}
