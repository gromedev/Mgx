using System.Net;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Error-body parsing must never throw. The constructor runs while a caller is already building
/// an exception, and 2.1's content path follows a redirect to SharePoint, OneDrive and CDN hosts
/// that are not Graph and do not emit the OData error envelope.
/// </summary>
public class ErrorBodyShapeTests
{
    [Theory]
    [InlineData("""{"error":{"code":"itemNotFound","message":"Item not found"}}""")]   // the normal shape
    [InlineData("""{"error":"just a string"}""")]                                      // error is not an object
    [InlineData("""{"error":{"code":404,"message":"numeric code"}}""")]                // code is not a string
    [InlineData("""{"error":{"message":{"value":"nested"}}}""")]                       // message is not a string
    [InlineData("""["an","array","root"]""")]                                          // root is an array
    [InlineData("\"a bare json string\"")]                                             // root is a string
    [InlineData("12345")]                                                              // root is a number
    [InlineData("null")]                                                               // root is null
    [InlineData("<html><body>503 from a CDN</body></html>")]                           // not JSON at all
    [InlineData("")]                                                                   // empty
    public void Never_throws_whatever_the_body_shape(string body)
    {
        var ex = new GraphServiceException(HttpStatusCode.ServiceUnavailable, body);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Still_extracts_a_graph_shaped_error()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.NotFound,
            """{"error":{"code":"itemNotFound","message":"Item not found"}}""");
        Assert.Contains("itemNotFound", ex.Message);
        Assert.Contains("Item not found", ex.Message);
    }

    [Fact]
    public void Extracts_a_message_nested_under_value()
    {
        var ex = new GraphServiceException(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"badRequest","message":{"value":"the real text"}}}""");
        Assert.Contains("the real text", ex.Message);
    }

    [Fact]
    public void Falls_back_to_the_status_line_when_the_shape_is_wrong()
    {
        var ex = new GraphServiceException(HttpStatusCode.ServiceUnavailable, """{"error":404}""");
        Assert.Contains("503", ex.Message);
    }
}
