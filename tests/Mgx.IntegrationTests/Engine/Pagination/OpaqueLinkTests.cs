using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A service-issued link is opaque: validation may refuse it, but a link that passes is
/// followed byte-identically - no re-encoding, no normalization, no re-parsing. A repaired
/// link is a different link, and Graph's skiptokens do not survive repair.
/// (GraphSDK-2488.)
/// </summary>
public class OpaqueLinkTests
{
    private static readonly Uri GraphHost = new("https://graph.microsoft.com/v1.0/users");

    [Fact]
    public void A_validated_link_is_the_original_string_byte_for_byte()
    {
        foreach (var link in HostileInputs.OpaqueLinks)
        {
            var validated = NextLinkValidator.Validate(link, GraphHost);
            Assert.NotNull(validated);
            Assert.Same(link, validated); // the same string instance - no rebuild happened
        }
    }

    [Fact]
    public void ValidateOrThrow_returns_the_original_string_too()
    {
        foreach (var link in HostileInputs.OpaqueLinks)
            Assert.Same(link, NextLinkValidator.ValidateOrThrow(link, GraphHost));
    }

    [Fact]
    public void A_checkpoint_round_trips_its_link_byte_for_byte()
    {
        foreach (var link in HostileInputs.OpaqueLinks)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mgx-oplink-{Guid.NewGuid():N}.json");
            try
            {
                new PaginationCheckpoint
                {
                    Resource = "https://graph.microsoft.com/v1.0/users",
                    NextLink = link,
                    ItemsCollected = 1,
                }.Save(path);
                var loaded = PaginationCheckpoint.Load(path);
                Assert.NotNull(loaded);
                Assert.Equal(link, loaded!.NextLink);
            }
            finally { File.Delete(path); }
        }
    }
}
