using System.Net;
using System.Net.Sockets;

namespace ProxyNET.Tests;

/// <summary>
/// A minimal TCP server standing in for the forwarding target.
/// </summary>
internal sealed class TestTargetServer : IAsyncDisposable
{
    public enum Mode
    {
        /// <summary>Echoes every chunk back as soon as it arrives.</summary>
        Echo,

        /// <summary>Reads until the peer half-closes and only then sends everything back.</summary>
        ReplyAfterEof
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;
    private readonly Task _acceptLoop;
    private readonly Mode _mode;

    public TestTargetServer(Mode mode)
    {
        _mode = mode;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        _listener.Dispose();

        try
        {
            await _acceptLoop;
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);

                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var peer = client;

        try
        {
            var stream = peer.GetStream();
            var buffer = new byte[4096];

            if (_mode == Mode.Echo)
            {
                int read;

                while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            }
            else
            {
                using var received = new MemoryStream();
                int read;

                while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                    received.Write(buffer, 0, read);

                await stream.WriteAsync(received.ToArray(), _cts.Token);
            }

            peer.Client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
        }
    }
}
