namespace Mgx.IntegrationTests;

/// <summary>
/// The shared hostile-input corpus. Each group generalizes a real failure; the issue
/// numbers are provenance, never scope (M365DSC and Graph SDK issue trackers).
/// </summary>
public static class HostileInputs
{
    /// <summary>OData filter values that break naive encoding.
    /// (GraphSDK-2709/2942 query corruption, GraphSDK-1947 '#'.)</summary>
    public static readonly string[] FilterValues =
    [
        "startsWith(displayName,'O''Brien')",   // escaped apostrophe
        "displayName eq 'A & B'",
        "displayName eq 'C#'",
        "displayName eq '50% off'",
        "displayName eq 'a+b'",
        "displayName eq 'x/y'",
        "displayName eq '?'",
        "displayName eq 'Müller'",
        "displayName eq '日本語'",
        "displayName eq '😀'",                  // surrogate pair
        "mail eq 'a b@contoso.com'",
    ];

    /// <summary>Path segments that must survive into the request path.
    /// (GraphSDK-1947, M365DSC-5354.)</summary>
    public static readonly string[] PathSegments =
    [
        "a#b.txt",
        "a b.txt",
        "100%.pdf",
        "it's.txt",
        "naïve.txt",
        "01ABCDEF!ABC:XYZ",                      // drive-item id shape
        "contoso.sharepoint.com,9f0e,ab12",      // site id shape
    ];

    /// <summary>Service-issued links that must be followed byte-identically.
    /// (GraphSDK-2488 nextLink handling.)</summary>
    public static readonly string[] OpaqueLinks =
    [
        "https://graph.microsoft.com/v1.0/users?$skiptoken=X%27443700...%27&$top=100",
        "https://graph.microsoft.com/v1.0/users?$skiptoken=a+b=c==&$select=id",
        "https://graph.microsoft.com/v1.0/users?$skiptoken=pre%20encoded%23frag",
        "https://graph.microsoft.com/v1.0/drives/b!YZCE_x-y/items?$skiptoken=1",
    ];
}
