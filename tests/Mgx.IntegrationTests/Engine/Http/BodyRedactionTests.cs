using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine.Http;

/// <summary>
/// -Debug body redaction. A pre-authenticated download URL fetches the file bytes with no bearer
/// token, and -Debug output is what users paste into issue reports, so this is a credential
/// control - and drive-content-triage.ps1 tells users it exists.
///
/// It previously had NO test at all: deleting both redaction passes left the entire suite green.
/// These pin both directions, because over-redaction is its own regression - @odata.nextLink is
/// how paging bugs get diagnosed.
/// </summary>
public class BodyRedactionTests
{
    private static string Trace(string body) =>
        GraphRequestTracer.FormatResponse(new HttpResponseMessage(HttpStatusCode.OK), 12, body);

    [Theory]
    // SharePoint / OneDrive for Business: capability in the query string.
    [InlineData("""{"@microsoft.graph.downloadUrl":"https://c-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=1&tempauth=eyJ0.LEAKED.sig&ApiVersion=2.0"}""", "LEAKED")]
    // Consumer OneDrive: capability in the PATH, which a query-only cut would miss entirely.
    [InlineData("""{"@microsoft.graph.downloadUrl":"https://public.bl.files.1drv.com/y4mLEAKED/file.bin"}""", "LEAKED")]
    // The older property name for the same thing.
    [InlineData("""{"@content.downloadUrl":"https://c-my.sharepoint.com/x?tempauth=LEAKED"}""", "LEAKED")]
    // Azure SAS.
    [InlineData("""{"uploadUrl":"https://s.blob.core.windows.net/c/b?sv=2021&sig=LEAKED&se=2026"}""", "LEAKED")]
    // Sharing link.
    [InlineData("""{"link":"https://c.sharepoint.com/g?guestaccesstoken=LEAKED"}""", "LEAKED")]
    // Presigned S3-style.
    [InlineData("""{"url":"https://b.s3.amazonaws.com/k?X-Amz-Signature=LEAKED&X-Amz-Expires=60"}""", "LEAKED")]
    // Nested inside a collection, which is how Graph actually returns driveItems.
    [InlineData("""{"value":[{"id":"a"},{"id":"b","@microsoft.graph.downloadUrl":"https://c-my.sharepoint.com/d?tempauth=LEAKED"}]}""", "LEAKED")]
    public void Redacts_pre_authenticated_urls(string body, string secret)
    {
        var trace = Trace(body);
        Assert.DoesNotContain(secret, trace, StringComparison.Ordinal);
        Assert.Contains("<redacted>", trace);
    }

    [Theory]
    // Paging tokens are diagnostics, not credentials. Losing these would remove the thing
    // pagination bugs are debugged with.
    [InlineData("""{"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=RFNwdAoAAQAAA"}""", "RFNwdAoAAQAAA")]
    [InlineData("""{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=KEEPME"}""", "KEEPME")]
    // Ordinary URLs on a driveItem.
    [InlineData("""{"webUrl":"https://c.sharepoint.com/Shared%20Documents/KEEPME.xlsx"}""", "KEEPME")]
    [InlineData("""{"siteUrl":"https://c.sharepoint.com/sites/KEEPME"}""", "KEEPME")]
    // The (?<![a-z])sig lookbehind must not eat these.
    [InlineData("""{"u":"https://example.com/x?design=KEEPME"}""", "KEEPME")]
    [InlineData("""{"u":"https://example.com/x?config=KEEPME"}""", "KEEPME")]
    [InlineData("""{"u":"https://example.com/x?assign=KEEPME"}""", "KEEPME")]
    public void Does_not_redact_diagnostics_or_ordinary_urls(string body, string keep)
    {
        Assert.Contains(keep, Trace(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Still_redacts_credential_named_properties()
    {
        var trace = Trace("""{"passwordCredential":{"secretText":"hunter2"}}""");
        Assert.DoesNotContain("hunter2", trace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three items, not two. Sanitize makes two passes that can each match a downloadUrl - one
    /// by property name, one by value - so with only two items a first-match-only regression
    /// was masked: pass one redacted FIRST, pass two redacted SECOND, and the test stayed green
    /// while every third and subsequent capability URL leaked.
    /// </summary>
    [Fact]
    public void Redacts_every_url_in_a_body_not_just_the_first()
    {
        var trace = Trace("""{"value":[{"@microsoft.graph.downloadUrl":"https://a.sharepoint.com/x?tempauth=FIRST"},{"@microsoft.graph.downloadUrl":"https://b.sharepoint.com/y?tempauth=SECOND"},{"@microsoft.graph.downloadUrl":"https://c.sharepoint.com/z?tempauth=THIRD"}]}""");
        Assert.DoesNotContain("FIRST", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("THIRD", trace, StringComparison.Ordinal);
    }
}
