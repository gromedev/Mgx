using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Verifies TransportDefaults constants and that SocketsHttpHandler
/// can be constructed with these values without throwing.
/// </summary>
[Collection("Pipeline")]
public class TransportConfigTests
{
    [Fact]
    public void TransportDefaults_ConnectTimeout_Is10Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), TransportDefaults.ConnectTimeout);
    }

    [Fact]
    public void TransportDefaults_PooledConnectionLifetime_Is2Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), TransportDefaults.PooledConnectionLifetime);
    }

    [Fact]
    public void TransportDefaults_MaxConnectionsPerServer_Is20()
    {
        Assert.Equal(20, TransportDefaults.MaxConnectionsPerServer);
    }

    [Fact]
    public void TransportDefaults_EnableMultipleHttp2Connections_IsTrue()
    {
        Assert.True(TransportDefaults.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void TransportDefaults_Decompression_IncludesGZipDeflateAndBrotli()
    {
        Assert.True(TransportDefaults.Decompression.HasFlag(DecompressionMethods.GZip));
        Assert.True(TransportDefaults.Decompression.HasFlag(DecompressionMethods.Deflate));
        Assert.True(TransportDefaults.Decompression.HasFlag(DecompressionMethods.Brotli));
    }

    [Fact]
    public void SocketsHttpHandler_AcceptsTransportDefaults()
    {
        // Verify SocketsHttpHandler can be constructed with all TransportDefaults values.
        // This catches if any constant is out of range for the runtime.
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TransportDefaults.ConnectTimeout,
            PooledConnectionLifetime = TransportDefaults.PooledConnectionLifetime,
            MaxConnectionsPerServer = TransportDefaults.MaxConnectionsPerServer,
            EnableMultipleHttp2Connections = TransportDefaults.EnableMultipleHttp2Connections,
            AutomaticDecompression = TransportDefaults.Decompression
        };

        Assert.Equal(TransportDefaults.ConnectTimeout, handler.ConnectTimeout);
        Assert.Equal(TransportDefaults.PooledConnectionLifetime, handler.PooledConnectionLifetime);
        Assert.Equal(TransportDefaults.MaxConnectionsPerServer, handler.MaxConnectionsPerServer);
        Assert.True(handler.EnableMultipleHttp2Connections);
        Assert.Equal(TransportDefaults.Decompression, handler.AutomaticDecompression);
    }

    [Fact]
    public void MaxRetryAfterSeconds_Is120()
    {
        Assert.Equal(120, ResilientGraphClientOptions.Default.MaxRetryAfterSeconds);
    }
}
