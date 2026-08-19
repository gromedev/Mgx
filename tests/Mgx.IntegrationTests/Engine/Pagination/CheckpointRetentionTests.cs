using System.Reflection;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// A valid checkpoint must survive an adoption that legitimately finds nothing to adopt.
///
/// A resumed JSONL run writes straight to the output and leaves no temp behind, so a SECOND
/// interruption reaches recovery with a checkpoint, an output, and no temp - adoption returns
/// false. If that is treated as "the output is missing", the checkpoint is deleted, the run
/// re-enumerates from the saved deltaLink, and the output is replaced with incremental changes
/// only. The baseline is then unrecoverable, because the delta token has already moved past it.
/// </summary>
public class CheckpointRetentionTests
{
    private static readonly MethodInfo Adopt =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool TryAdopt(string outputPath, long itemCount) =>
        (bool)Adopt.Invoke(null, [outputPath, itemCount])!;

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"mgx-ckpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// The exact second-interruption state: checkpoint + output + NO temp. Adoption must fail,
    /// and the caller must NOT read that as a missing output.
    /// </summary>
    [Fact]
    public void Adoption_fails_when_a_resumed_run_left_no_temp()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"baseline-1\"}", "{\"id\":\"baseline-2\"}"]);

            Assert.False(TryAdopt(output, 2));

            // The caller distinguishes these two facts. Conflating them is what destroyed data:
            // adoption failing is routine; the output being absent is not.
            Assert.True(File.Exists(output));
            Assert.Equal(2, File.ReadAllLines(output).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_failed_adoption_leaves_the_existing_output_untouched()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"baseline\"}"]);
            // Temp holds fewer lines than the checkpoint promises: adoption must refuse.
            File.WriteAllLines($"{output}.aaa.tmp", ["{\"id\":\"partial\"}"]);

            Assert.False(TryAdopt(output, 99));

            Assert.Equal(["{\"id\":\"baseline\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists($"{output}.adopt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// PaginationCheckpoint.Delete is what the caller invokes on the missing-output path. Guard
    /// against a future edit reintroducing an unconditional delete by pinning the distinction
    /// the caller has to make.
    /// </summary>
    [Fact]
    public void Checkpoint_and_output_are_independent_facts()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            var cp = Path.Combine(dir, "out.checkpoint");
            File.WriteAllLines(output, ["{\"id\":\"kept\"}"]);
            new PaginationCheckpoint
            {
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=abc",
                Resource = "/users/delta",
                ItemsCollected = 1
            }.Save(cp);

            Assert.False(TryAdopt(output, 1));   // no temp: routine
            Assert.True(File.Exists(cp));        // and therefore no reason to drop the checkpoint
            Assert.True(File.Exists(output));
        }
        finally { Directory.Delete(dir, true); }
    }
}
