using Mgx.Cmdlets.Base;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests;

/// <summary>
/// The transport seam is a pair. GetClient takes the epoch, then the transport, then the epoch
/// again and reads both again on a change; a scope arms the transport before it bumps the epoch.
/// Either half on its own is wrong: a reader that takes the transport first can pair one the
/// scope has already retired with the epoch that retired it, and then answers from that
/// transport for as long as the cmdlet instance lives.
///
/// The interleaving cannot be observed without racing the two, so what is pinned here is the
/// write order the reader depends on, seen from between the two writes.
/// </summary>
[Collection("Pipeline")]
public class TransportSeamOrderTests
{
    [Fact]
    public void A_scope_arms_its_transport_before_it_bumps_the_epoch()
    {
        using var outer = MgxTransportScope.Inject(new StubHttpMessageHandler());
        var outerTransport = MgxCmdletBase.s_testTransport;
        Assert.NotNull(outerTransport);
        var epochBefore = Volatile.Read(ref MgxCmdletBase.s_testTransportEpoch);

        HttpClient? transportInWindow = null;
        var epochInWindow = 0;
        var windows = 0;

        using (var inner = MgxTransportScope.Inject(new StubHttpMessageHandler()))
        {
            // Arming happened in the constructor: the transport is the inner scope's and the
            // epoch has moved once, so a client cached over the outer one no longer matches.
            Assert.NotSame(outerTransport, MgxCmdletBase.s_testTransport);
            Assert.Equal(epochBefore + 1, Volatile.Read(ref MgxCmdletBase.s_testTransportEpoch));

            inner.BetweenPublishWrites = () =>
            {
                windows++;
                transportInWindow = MgxCmdletBase.s_testTransport;
                epochInWindow = Volatile.Read(ref MgxCmdletBase.s_testTransportEpoch);
            };
        }

        Assert.Equal(1, windows);
        Assert.Same(outerTransport, transportInWindow);           // the transport goes back first
        Assert.Equal(epochBefore + 1, epochInWindow);             // and only then does the epoch move
        Assert.Equal(epochBefore + 2, Volatile.Read(ref MgxCmdletBase.s_testTransportEpoch));
        Assert.Same(outerTransport, MgxCmdletBase.s_testTransport);
    }
}
