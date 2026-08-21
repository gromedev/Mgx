using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Both state files are written to "&lt;path&gt;.tmp" and renamed, so a crash cannot leave a
/// half-written file. A save that FAILS should not leave the staging file either - adoption and
/// recovery already have to reason about stray files beside the output.
/// </summary>
[Collection("Pipeline")]
public class AtomicSaveHygieneTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-atomic-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void A_failed_checkpoint_save_leaves_no_staging_file()
    {
        var dir = NewDir();
        var target = Path.Combine(dir, "run.checkpoint");
        try
        {
            // The rename cannot land on a directory, so the save fails after the staging file
            // has been written - the window this is about.
            Directory.CreateDirectory(target);

            var cp = new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=x",
                ItemsCollected = 1
            };
            Assert.ThrowsAny<Exception>(() => cp.Save(target));

            Assert.False(File.Exists(target + ".tmp"),
                "the staging file outlived the save that failed to promote it");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>
/// A delta state file from before this field existed carries no apiVersion, so the mismatch
/// check is skipped and the run proceeds against whatever version the stored deltaLink names.
/// Recording the REQUESTED version at that point stamped one the token was never issued by, and
/// every later run refused with advice pointing at the wrong version.
/// </summary>
[Collection("Pipeline")]
public class DeltaApiVersionStampTests
{
    private static string? Stamp(string? link)
    {
        var m = typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).GetMethod(
            "ApiVersionOfLink",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string?)m.Invoke(null, [link]);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc", "v1.0")]
    [InlineData("https://graph.microsoft.com/beta/users/delta?$deltatoken=abc", "beta")]
    public void The_version_comes_from_the_link(string link, string expected)
    {
        Assert.Equal(expected, Stamp(link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://graph.microsoft.com/v9.9/users/delta")]
    public void An_unreadable_link_falls_back_to_the_request(string? link)
    {
        Assert.Null(Stamp(link));
    }
}
