namespace Mgx.Engine.Http;

/// <summary>
/// Validates pre-authenticated content download URLs (hop-2 targets: the 302 Location from a
/// Graph /content request, or @microsoft.graph.downloadUrl off a driveItem) before the
/// token-free HTTP client fetches them.
///
/// Threat model, precisely: the two-hop split guarantees the bearer token never reaches the
/// download host, and this allowlist keeps the token-free client pointed at Microsoft-operated
/// infrastructure (no SSRF into arbitrary hosts). It does NOT guarantee content origin - any
/// tenant on earth controls a *.sharepoint.com subdomain, so an allowlisted host is a host
/// that cannot steal the token, not a host whose bytes are trustworthy.
///
/// Deliberately separate from NextLinkValidator, which stays same-authority-only: pagination
/// must never leave the Graph host, downloads legitimately do.
/// </summary>
public static class DownloadUrlValidator
{
    // Dot-anchored suffixes: the leading dot means "some subdomain of", so
    // evilsharepoint.com and sharepoint.com.evil.example never match. Static in 2.1 -
    // no user knob; nothing in scope downloads from outside this set.
    private static readonly string[] AllowedHostSuffixes =
    [
        ".sharepoint.com",
        ".sharepoint.us",
        ".sharepoint.cn",
        ".sharepointonline.com",
        ".files.1drv.com",
        ".svc.ms"
    ];

    /// <summary>
    /// Returns the URL unchanged when it passes every check, or null when the download must
    /// be refused: non-HTTPS (token-free or not, bytes travel plaintext), a non-default port
    /// (suffix matching alone would admit tenant.sharepoint.com:8443 shapes), embedded
    /// userinfo (classic parser-confusion trick), or a host outside the allowlist.
    /// </summary>
    public static string? Validate(string? downloadUrl)
    {
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return null;

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!uri.IsDefaultPort) return null;

        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        // IdnHost is the punycode-normalized form, so lookalike Unicode hosts are compared
        // in the same alphabet the resolver will actually use.
        var host = uri.IdnHost;
        foreach (var suffix in AllowedHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return downloadUrl;
        }

        return null;
    }
}
