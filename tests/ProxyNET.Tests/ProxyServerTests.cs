using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyNET.Configuration;
using Xunit;

namespace ProxyNET.Tests;

public class ProxyServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ForwardsDataInBothDirections()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes("round trip through the proxy");

        await stream.WriteAsync(payload);

        var response = await ReadExactlyAsync(stream, payload.Length);

        Assert.Equal(payload, response);
    }

    [Fact]
    public async Task ForwardsDataWithoutWaitingForData()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.WaitForData = false);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes("no zero byte receive in between");

        await stream.WriteAsync(payload);

        var response = await ReadExactlyAsync(stream, payload.Length);

        Assert.Equal(payload, response);
    }

    [Fact]
    public async Task HalfClosedPeer_StillReceivesTheResponse()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.ReplyAfterEof);
        await using var proxy = new ProxyHarness(target.Port);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes("request that is answered after the half-close");

        await stream.WriteAsync(payload);

        client.Client.Shutdown(SocketShutdown.Send);

        var response = await ReadToEndAsync(stream);

        Assert.Equal(payload, response);
    }

    [Fact]
    public async Task ForwardsPayloadsLargerThanTheSessionBuffer()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.BufferSize = 4096);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = new byte[4 * 1024 * 1024];

        Random.Shared.NextBytes(payload);

        var send = stream.WriteAsync(payload).AsTask();
        var received = await ReadExactlyAsync(stream, payload.Length, TimeSpan.FromSeconds(60));

        await send.WaitAsync(Timeout);

        Assert.True(payload.AsSpan().SequenceEqual(received));
    }

    [Fact]
    public async Task CountsSessionsAndForwardedBytes()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = new byte[2 * 1024 * 1024];

        Random.Shared.NextBytes(payload);

        var send = stream.WriteAsync(payload).AsTask();

        await ReadExactlyAsync(stream, payload.Length, TimeSpan.FromSeconds(60));
        await send.WaitAsync(Timeout);

        client.Client.Shutdown(SocketShutdown.Send);

        await WaitForAsync(() => proxy.Server.ActiveSessions == 0);

        var stats = proxy.Server.Stats.Snapshot();

        Assert.Equal(1, stats.Accepted);
        Assert.Equal(0, stats.Rejected);
        Assert.Equal(0, stats.Failed);
        Assert.Equal(payload.Length, stats.BytesToTarget);
        Assert.Equal(payload.Length, stats.BytesToPeer);
    }

    [Fact]
    public async Task MaxConnections_ClosesConnectionsBeyondTheLimit()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.MaxConnections = 1);

        using var accepted = await ConnectAsync(proxy.Port);

        var acceptedStream = accepted.GetStream();

        await acceptedStream.WriteAsync(Encoding.UTF8.GetBytes("first"));
        await ReadExactlyAsync(acceptedStream, "first".Length);

        using var rejected = await ConnectAsync(proxy.Port);

        Assert.True(await IsClosedAsync(rejected.GetStream()));
        await WaitForAsync(() => proxy.Server.Stats.Snapshot().Rejected == 1);

        await acceptedStream.WriteAsync(Encoding.UTF8.GetBytes("still alive"));

        Assert.Equal(Encoding.UTF8.GetBytes("still alive"),
            await ReadExactlyAsync(acceptedStream, "still alive".Length));
    }

    [Fact]
    public async Task UnreachableTarget_ClosesThePeerConnectionAndIsCounted()
    {
        await using var proxy = new ProxyHarness(GetUnusedPort());

        using var client = await ConnectAsync(proxy.Port);

        Assert.True(await IsClosedAsync(client.GetStream()));
        await WaitForAsync(() => proxy.Server.Stats.Snapshot().Failed == 1);
    }

    [Fact]
    public async Task IdleTimeout_AbortsTheSession()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.IdleTimeout = 500);

        using var client = await ConnectAsync(proxy.Port);

        Assert.True(await IsClosedAsync(client.GetStream()));
        await WaitForAsync(() => proxy.Server.Stats.Snapshot().IdleTimeouts == 1);
    }

    [Fact]
    public async Task LegacyReadTimeout_AbortsTheSession()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.ReadTimeout = 500);

        using var client = await ConnectAsync(proxy.Port);

        Assert.True(await IsClosedAsync(client.GetStream()));
    }

    [Fact]
    public async Task IdleTimeout_KeepsBusySessionsAlive()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.IdleTimeout = 1000);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes("keep going");

        for (var round = 0; round < 8; round++)
        {
            await stream.WriteAsync(payload);
            await ReadExactlyAsync(stream, payload.Length);
            await Task.Delay(300);
        }

        Assert.Equal(1, proxy.Server.ActiveSessions);
        Assert.Equal(0, proxy.Server.Stats.Snapshot().IdleTimeouts);
    }

    [Fact]
    public async Task WriteTimeout_IsAppliedPerWrite()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.WriteTimeout = 5000);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes("written within the deadline");

        await stream.WriteAsync(payload);

        Assert.Equal(payload, await ReadExactlyAsync(stream, payload.Length));
    }

    [Fact]
    public async Task Cancellation_StopsTheServer()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port, config => config.IdleTimeout = 60_000);

        await proxy.Cancellation.CancelAsync();
        await proxy.RunTask.WaitAsync(Timeout);

        Assert.True(proxy.RunTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopAsync_WaitsForRunningSessions()
    {
        await using var target = new TestTargetServer(TestTargetServer.Mode.Echo);
        await using var proxy = new ProxyHarness(target.Port);

        using var client = await ConnectAsync(proxy.Port);

        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("keep the session open"));
        await ReadExactlyAsync(stream, "keep the session open".Length);

        Assert.Equal(1, proxy.Server.ActiveSessions);

        await proxy.Cancellation.CancelAsync();
        await proxy.Server.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, proxy.Server.ActiveSessions);
    }

    [Fact]
    public void Start_InvalidListenAddress_ThrowsOnConstruction()
    {
        var config = new ForwardingConfig
        {
            Address = "not-an-address",
            Port = 0,
            Forward = new ForwardConfig { Address = "127.0.0.1", Port = 8090 }
        };

        Assert.Throws<ArgumentException>(() => new ProxyServer(config));
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        using var server = new ProxyServer(CreateConfig(GetUnusedPort()));

        server.Start();

        Assert.Throws<InvalidOperationException>(() => server.Start());
    }

    [Fact]
    public async Task RunAsync_WithoutStart_Throws()
    {
        using var server = new ProxyServer(CreateConfig(GetUnusedPort()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync(CancellationToken.None));
    }

    [Fact]
    public void Start_PortZero_ExposesTheBoundEndpoint()
    {
        using var server = new ProxyServer(CreateConfig(GetUnusedPort()));

        Assert.Null(server.BoundEndpoint);

        server.Start();

        Assert.NotNull(server.BoundEndpoint);
        Assert.NotEqual(0, server.BoundEndpoint!.Port);
    }

    private static ForwardingConfig CreateConfig(int targetPort) => new()
    {
        Address = "127.0.0.1",
        Port = 0,
        Forward = new ForwardConfig { Address = "127.0.0.1", Port = targetPort }
    };

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);

        return client;
    }

    private static int GetUnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);

        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(25);

        Assert.True(condition());
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, TimeSpan? timeout = null)
    {
        var buffer = new byte[count];

        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(timeout ?? Timeout);

        return buffer;
    }

    private static async Task<byte[]> ReadToEndAsync(NetworkStream stream)
    {
        using var received = new MemoryStream();

        await stream.CopyToAsync(received).WaitAsync(Timeout);

        return received.ToArray();
    }

    private static async Task<bool> IsClosedAsync(NetworkStream stream)
    {
        try
        {
            return await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout) == 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a proxy on an operating system assigned port for the duration of a test.
    /// </summary>
    private sealed class ProxyHarness : IAsyncDisposable
    {
        public ProxyHarness(int targetPort, Action<ForwardingConfig>? configure = null)
        {
            var config = CreateConfig(targetPort);

            configure?.Invoke(config);

            Server = new ProxyServer(config);
            Server.Start();

            Port = Server.BoundEndpoint!.Port;
            RunTask = Server.RunAsync(Cancellation.Token);
        }

        public ProxyServer Server { get; }

        public int Port { get; }

        public Task RunTask { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Cancellation.CancelAsync();

            try
            {
                await RunTask.WaitAsync(Timeout);
            }
            catch
            {
            }

            await Server.StopAsync(TimeSpan.FromSeconds(5));

            Server.Dispose();
            Cancellation.Dispose();
        }
    }
}
