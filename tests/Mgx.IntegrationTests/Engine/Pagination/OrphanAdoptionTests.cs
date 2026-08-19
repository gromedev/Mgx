using System.Reflection;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// Orphan-temp adoption after a JSONL sync is interrupted. The case that matters is the
/// steady-state one: a run that SUCCEEDED, then a run that crashed. Adoption previously
/// required the output file to be absent, so that sequence skipped it and the crashed run's
/// items were dropped while the delta token advanced past them.
/// </summary>
public class OrphanAdoptionTests
{
    private static readonly MethodInfo Adopt =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryAdoptOrphanedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool Invoke(string outputPath, long itemCount) =>
        (bool)Adopt.Invoke(null, [outputPath, itemCount])!;

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"mgx-adopt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Adopts_the_temp_when_no_output_exists_yet()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines($"{output}.abc.tmp", ["{\"id\":\"a\"}", "{\"id\":\"b\"}", "{\"id\":\"c\"}"]);

            Assert.True(Invoke(output, 2));

            // Trimmed to the checkpointed count: the third line was written after the last flush.
            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Preserves_a_completed_runs_output_and_appends_the_crashed_runs_items()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            // Run 1 completed.
            File.WriteAllLines(output, ["{\"id\":\"run1-1\"}", "{\"id\":\"run1-2\"}"]);
            // Run 2 crashed: temp holds 3 lines, checkpoint promises 2.
            File.WriteAllLines($"{output}.def.tmp", ["{\"id\":\"run2-1\"}", "{\"id\":\"run2-2\"}", "{\"id\":\"unflushed\"}"]);

            Assert.True(Invoke(output, 2));

            Assert.Equal(
                ["{\"id\":\"run1-1\"}", "{\"id\":\"run1-2\"}", "{\"id\":\"run2-1\"}", "{\"id\":\"run2-2\"}"],
                File.ReadAllLines(output));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Refuses_when_the_temp_holds_less_than_the_checkpoint_promises()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"kept\"}"]);
            File.WriteAllLines($"{output}.ghi.tmp", ["{\"id\":\"only-one\"}"]);

            Assert.False(Invoke(output, 5));

            // A refused adoption must not touch the existing output, nor leave staging behind.
            Assert.Equal(["{\"id\":\"kept\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists($"{output}.adopt"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Does_nothing_when_there_is_no_temp()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllLines(output, ["{\"id\":\"kept\"}"]);
            Assert.False(Invoke(output, 3));
            Assert.Equal(["{\"id\":\"kept\"}"], File.ReadAllLines(output));
        }
        finally { Directory.Delete(dir, true); }
    }
}
