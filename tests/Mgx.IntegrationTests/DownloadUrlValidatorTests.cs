using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// The hop-2 download allowlist. Companion to SsrfValidationTests (which covers
/// NextLinkValidator's same-authority rule); this validator is the one place a request is
/// allowed to leave the Graph host, so every bypass shape gets a deny test.
/// </summary>
public class DownloadUrlValidatorTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com/_layouts/15/download.aspx?UniqueId=abc&tempauth=xyz")]
    [InlineData("https://contoso-my.sharepoint.com/personal/user/_layouts/15/download.aspx?share=abc")]
    [InlineData("https://tenant.sharepoint.us/download?x=1")]
    [InlineData("https://tenant.sharepoint.cn/download")]
    [InlineData("https://tenant.sharepointonline.com/file")]
    [InlineData("https://public.bn1303.files.1drv.com/y4mabc/file.jpg")]
    [InlineData("https://southindia1-mediap.svc.ms/transform/thumbnail?provider=spo")]
    public void Allows_documented_microsoft_download_hosts(string url) =>
        Assert.Equal(url, DownloadUrlValidator.Validate(url));

    [Theory]
    [InlineData("http://contoso.sharepoint.com/file")]                       // scheme downgrade
    [InlineData("https://evilsharepoint.com/file")]                          // suffix not dot-anchored
    [InlineData("https://sharepoint.com/file")]                              // apex, no subdomain
    [InlineData("https://contoso.sharepoint.com.evil.example/file")]         // suffix embedded mid-host
    [InlineData("https://contoso.sharepoint.com:8443/file")]                 // non-default port
    [InlineData("https://user@contoso.sharepoint.com/file")]                 // userinfo trick
    [InlineData("https://user:pass@contoso.sharepoint.com/file")]            // userinfo trick
    [InlineData("https://mystore.blob.core.windows.net/container/file?sig=x")] // any Azure customer owns one
    [InlineData("https://evil.example/redirect?to=contoso.sharepoint.com")]  // suffix only in query
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Denies_everything_else(string url) =>
        Assert.Null(DownloadUrlValidator.Validate(url));

    [Fact]
    public void Denies_null() =>
        Assert.Null(DownloadUrlValidator.Validate(null));

    [Fact]
    public void Denies_unicode_lookalike_hosts()
    {
        // "ѕharepoint" with a Cyrillic dze: punycode-normalizes to xn--, never matches.
        Assert.Null(DownloadUrlValidator.Validate("https://contoso.ѕharepoint.com/file"));
    }
}
