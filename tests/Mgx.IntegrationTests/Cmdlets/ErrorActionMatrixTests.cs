using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// about_Mgx_Errors states the -ErrorAction and -ErrorVariable contract in prose and names only
/// Stop and SilentlyContinue. This drives all twenty-four preference-by-context cells and asserts,
/// per cell, whether the pipeline terminates, whether a record reaches $Error, whether
/// -ErrorVariable collects it, and whether anything is displayed.
///
/// What was covered before this file, and by what: BatchErrorSurfacingTests pinned the batch
/// context under Stop twice (termination alone, with and without a dead-letter path) and under the
/// default preference once (one record on the error stream). Three cells of twenty-four, none of
/// them across all three properties; -ErrorVariable was asserted nowhere and Ignore appeared
/// nowhere.
///
/// All four preferences are driven as common parameters. Ignore cannot be set as a preference
/// variable, so a preference-variable form could not cover the same grid.
///
/// (Corpus: M365DSC-7273 sustained throttling - the throttling cells generalize it to what a
/// caller sees when a 429 outlives its retries; M365DSC-7198 batch semantics - the batch cells
/// extend it from Stop to all four preferences.)
/// </summary>
[Collection("Pipeline")]
public class ErrorActionMatrixTests
{
    private const string NotFoundBody =
        """{"error":{"code":"Request_ResourceNotFound","message":"u1 does not exist"}}""";
    private const string ServerErrorBody =
        """{"error":{"code":"InternalServerError","message":"boom"}}""";
    private const string ThrottledBody =
        """{"error":{"code":"TooManyRequests","message":"slow down"}}""";
    private const string OneOkOneNotFound = """
    { "responses": [
        { "id": "1", "status": 200, "body": { "id": "u1" } },
        { "id": "2", "status": 404, "body": { "error": { "code": "Request_ResourceNotFound", "message": "u2 does not exist" } } }
    ] }
    """;

    private static readonly string[] Contexts =
    [
        "DirectRequest", "ExhaustedRetry", "Batch", "Pagination", "CircuitBreaker", "Throttling"
    ];

    private static readonly string[] Preferences =
    [
        "Stop", "Continue", "SilentlyContinue", "Ignore"
    ];

    public static TheoryData<string, string> Cells
    {
        get
        {
            var cells = new TheoryData<string, string>();
            foreach (var context in Contexts)
                foreach (var preference in Preferences)
                    cells.Add(context, preference);
            return cells;
        }
    }

    /// <summary>The wire, the options, an optional priming run, and the pipeline under test.</summary>
    private sealed record Cell(
        MockHttpHandler Wire,
        ResilientGraphClientOptions Options,
        string? Prime,
        string Pipeline);

    private static ResilientGraphClientOptions Fast => new()
    {
        NoRateLimit = true,
        MaxRetryAttempts = 1,
        MaxRetryAfterSeconds = 1
    };

    private static Cell BuildCell(string context)
    {
        var wire = new MockHttpHandler();
        switch (context)
        {
            // A 404 the retry policy does not retry: one request, one record.
            case "DirectRequest":
                wire.SetDefaultResponse(HttpStatusCode.NotFound, NotFoundBody);
                return new Cell(wire, Fast, null, "Invoke-MgxRequest -Uri /users/u1");

            // Every attempt fails, so the record is written after the last retry rather than
            // on the first failure - the case about_Mgx_Errors calls out under RETRIES.
            case "ExhaustedRetry":
                wire.SetDefaultResponse(HttpStatusCode.ServiceUnavailable, ServerErrorBody);
                return new Cell(wire, Fast, null, "Invoke-MgxRequest -Uri /users/u1");

            // The chunk POST succeeds and one item inside it answers 404.
            case "Batch":
                wire.QueueResponse(HttpStatusCode.OK, OneOkOneNotFound);
                return new Cell(wire, Fast, null,
                    "Invoke-MgxBatchRequest -Uri @('/users/u1','/users/u2') -Method GET");

            // Page one is delivered, so output exists before the failure the record describes.
            case "Pagination":
                wire.QueueResponse(HttpStatusCode.OK, TestData.UsersPage1);
                wire.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerErrorBody);
                return new Cell(wire, Fast, null, "Invoke-MgxRequest -Uri /users -All");

            // The priming run's two failed attempts meet the minimum throughput at a failure
            // ratio of 1.0, so the breaker is open before the measured pipeline runs.
            case "CircuitBreaker":
                wire.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerErrorBody);
                return new Cell(
                    wire,
                    new ResilientGraphClientOptions
                    {
                        NoRateLimit = true,
                        MaxRetryAttempts = 1,
                        MaxRetryAfterSeconds = 1,
                        CircuitBreakerMinThroughput = 2,
                        CircuitBreakerFailureRatio = 0.5,
                        CircuitBreakerDurationSeconds = 30
                    },
                    "Invoke-MgxRequest -Uri /users/u1 -ErrorAction SilentlyContinue | Out-Null",
                    "Invoke-MgxRequest -Uri /users/u1");

            case "Throttling":
                wire.SetDefaultResponse(HttpStatusCode.TooManyRequests, ThrottledBody,
                    new Dictionary<string, string> { ["Retry-After"] = "0" });
                return new Cell(wire, Fast, null, "Invoke-MgxRequest -Uri /users/u1");

            default:
                throw new ArgumentOutOfRangeException(nameof(context), context, "unknown failure context");
        }
    }

    private static System.Management.Automation.PowerShell CreateShell()
    {
        var ps = System.Management.Automation.PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    private sealed record Outcome(
        bool Terminated,
        int ErrorCount,
        int VariableCount,
        string FirstErrorId,
        int ErrorStreamCount,
        bool FailureServed);

    /// <summary>
    /// Runs one cell inside PowerShell so the preference is interpreted where it is interpreted
    /// in production - by the engine, not by a caller reading a C# property.
    /// </summary>
    private static Outcome Drive(string context, string preference)
    {
        var cell = BuildCell(context);
        using var transport = MgxTransportScope.Inject(cell.Wire, options: cell.Options);
        using var ps = CreateShell();

        // The priming run goes through its own Invoke so the error stream can be emptied after
        // it. That stream is the only thing telling Continue from SilentlyContinue - both leave
        // a record in $Error and in -ErrorVariable, and only Continue displays one.
        if (cell.Prime != null)
        {
            ps.AddScript(cell.Prime);
            ps.Invoke();
            ps.Commands.Clear();
        }

        ps.Streams.Error.Clear();

        // $Error is cleared after any priming run, so the count measures the pipeline under test.
        var script = $$"""
            $Error.Clear()
            $ev = $null
            $terminated = $false
            try { {{cell.Pipeline}} -ErrorAction {{preference}} -ErrorVariable ev | Out-Null }
            catch { $terminated = $true }
            [pscustomobject]@{
                Terminated    = $terminated
                ErrorCount    = $Error.Count
                VariableCount = @($ev).Where({ $null -ne $_ }).Count
                FirstErrorId  = if ($Error.Count) { @($Error)[-1].FullyQualifiedErrorId } else { '' }
            }
            """;

        ps.AddScript(script);
        var results = ps.Invoke();
        var outcome = Assert.Single(results);
        return new Outcome(
            (bool)outcome.Properties["Terminated"].Value,
            (int)outcome.Properties["ErrorCount"].Value,
            (int)outcome.Properties["VariableCount"].Value,
            (string)outcome.Properties["FirstErrorId"].Value,
            ps.Streams.Error.Count,
            FailureServed(context, cell.Wire));
    }

    /// <summary>
    /// That the wire produced the failure this context is about. Under Ignore every count the
    /// grid reads is zero whether the request failed or succeeded, so the cell needs a signal
    /// that does not come from the pipeline. A batch failure travels inside a 200, so there the
    /// signal is the chunk going out and the queued response still carrying a failed item.
    /// </summary>
    private static bool FailureServed(string context, MockHttpHandler wire) => context == "Batch"
        ? wire.RequestCount > 0 && OneOkOneNotFound.Contains("\"status\": 404", StringComparison.Ordinal)
        : wire.ServedStatusCodes.Exists(status => (int)status >= 400);

    /// <summary>
    /// The record each context is supposed to produce. Without this a context that quietly stopped
    /// failing the way it claims - a 404 answered before pagination started, say - would still
    /// satisfy every count in the grid.
    /// </summary>
    private static string ExpectedErrorId(string context) => context switch
    {
        "DirectRequest" => "Request_ResourceNotFound",
        "ExhaustedRetry" => "InternalServerError",
        "Batch" => "BatchItemError",
        "Pagination" => "InternalServerError",
        "CircuitBreaker" => "CircuitBroken",
        "Throttling" => "TooManyRequests",
        _ => throw new ArgumentOutOfRangeException(nameof(context), context, "unknown failure context")
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_preference_decides_termination_the_error_stack_and_the_variable(
        string context, string preference)
    {
        var outcome = Drive(context, preference);

        Assert.Equal(preference == "Stop", outcome.Terminated);

        // Only Continue displays the record. SilentlyContinue keeps it, in $Error and in
        // -ErrorVariable, and shows nothing - which is the whole difference between them, and
        // the one about_Mgx_Errors describes.
        Assert.Equal(preference == "Continue", outcome.ErrorStreamCount > 0);

        if (preference == "Ignore")
        {
            // Ignore is the one preference that leaves no trace: nothing to find afterwards,
            // by either route. Every count below holds just as well for a request that
            // succeeded, so the wire has to say it produced the failure this cell is about.
            Assert.True(outcome.FailureServed,
                $"{context}/{preference}: the wire never served a failure");
            Assert.Equal(0, outcome.ErrorCount);
            Assert.Equal(0, outcome.VariableCount);
        }
        else
        {
            Assert.True(outcome.ErrorCount > 0,
                $"{context}/{preference}: no record reached $Error");
            Assert.True(outcome.VariableCount > 0,
                $"{context}/{preference}: -ErrorVariable collected nothing");
            Assert.StartsWith(ExpectedErrorId(context), outcome.FirstErrorId);
        }
    }
}
