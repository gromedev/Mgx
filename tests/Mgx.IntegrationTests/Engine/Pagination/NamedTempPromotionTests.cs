using System.Reflection;

namespace Mgx.IntegrationTests.Engine.Pagination;

/// <summary>
/// Promotion of the temp a checkpoint names, which is how every checkpoint this release writes
/// recovers: the file is taken by name and by the length recorded for it, so nothing has to be
/// guessed about which run's items are in it. What the name does not say is whether that run is
/// finished. Promotion replaces the output with those bytes and then unlinks the temp, so the
/// same claim adoption takes in OrphanAdoptionTests has to be taken here - a file nobody can be
/// found holding is an interrupted run's leftovers, and one that cannot be claimed belongs to a
/// run that is still writing it.
/// </summary>
public class NamedTempPromotionTests
{
    private static readonly MethodInfo Promote =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "TryPromoteNamedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo CanPromote =
        typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetMethod(
            "CanPromoteNamedTemp", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool Invoke(string outputPath, string tempFileName, long dataLength) =>
        (bool)Promote.Invoke(null, [outputPath, tempFileName, dataLength])!;

    private static bool InvokeCan(string outputPath, string tempFileName, long dataLength) =>
        (bool)CanPromote.Invoke(null, [outputPath, tempFileName, dataLength])!;

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"mgx-promote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// The state a crash leaves: the previous run's output, this run's items in the temp beside
    /// it, and a checkpoint naming both. The temp replaces the output, trimmed to the length
    /// recorded, and goes.
    /// </summary>
    [Fact]
    public void Promotes_a_named_temp_nothing_holds()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"a\"}\n{\"id\":\"b\"}\n");
            // The writer's position at the last flush, which is what a checkpoint records.
            var counted = new FileInfo(temp).Length;
            File.AppendAllText(temp, "{\"id\":\"after-the-flush\"}\n");

            Assert.True(InvokeCan(output, Path.GetFileName(temp), counted));
            Assert.True(Invoke(output, Path.GetFileName(temp), counted));

            Assert.Equal(["{\"id\":\"a\"}", "{\"id\":\"b\"}"], File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The same three files, with the run that wrote the temp still writing it. Promotion
    /// copied its rows into this run's output and unlinked the file underneath it, so that run
    /// wrote into an unlinked inode and its own closing move failed against a name that was no
    /// longer there. Being named by a checkpoint says which file holds the items; it says
    /// nothing about whether anyone still has it open.
    /// </summary>
    [Fact]
    public void Refuses_a_named_temp_a_live_run_still_holds()
    {
        var dir = NewDir();
        try
        {
            var output = Path.Combine(dir, "out.jsonl");
            File.WriteAllText(output, "{\"id\":\"old\"}\n");
            var live = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");

            // Opened the way an export opens its own temp: new StreamWriter(path, append).
            using (var writer = new StreamWriter(live, append: false))
            {
                writer.WriteLine("{\"id\":\"a\"}");
                writer.WriteLine("{\"id\":\"b\"}");
                writer.Flush();
                var counted = new FileInfo(live).Length;
                Assert.True(counted > 0);

                Assert.False(InvokeCan(output, Path.GetFileName(live), counted));
                Assert.False(Invoke(output, Path.GetFileName(live), counted));

                Assert.Equal(["{\"id\":\"old\"}"], File.ReadAllLines(output));
                Assert.False(File.Exists($"{output}.adopt"));
                Assert.True(File.Exists(live), "a running export's temp was promoted out from under it");

                // The run that owns it goes on writing into the file it still has.
                writer.WriteLine("{\"id\":\"c\"}");
            }

            Assert.Equal(
                ["{\"id\":\"a\"}", "{\"id\":\"b\"}", "{\"id\":\"c\"}"],
                File.ReadAllLines(live));
        }
        finally { Directory.Delete(dir, true); }
    }
}
