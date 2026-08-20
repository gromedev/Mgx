using System.Reflection;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// Orphan-temp adoption after a JSONL run is interrupted, for checkpoints that predate the
/// recorded temp name and length. With only a line count and a glob to go on, adoption is
/// safe solely when no output exists; against an existing output it must refuse and leave the
/// caller to re-enumerate, since nothing ties the newest temp to the run the checkpoint is
/// actually about.
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
    public void Refuses_when_an_output_already_exists()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            // Run 1 completed.
            File.WriteAllLines(output, ["{\"id\":\"run1-1\"}", "{\"id\":\"run1-2\"}"]);
            // Run 2 crashed: a temp is on disk, but nothing says it is run 2's rather than a
            // survivor of some unrelated enumeration against the same output name.
            File.WriteAllLines($"{output}.def.tmp", ["{\"id\":\"run2-1\"}", "{\"id\":\"run2-2\"}", "{\"id\":\"unflushed\"}"]);

            Assert.False(Invoke(output, 2));

            // The output is untouched; recovery falls to re-enumeration, not a guessed merge.
            Assert.Equal(
                ["{\"id\":\"run1-1\"}", "{\"id\":\"run1-2\"}"],
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
