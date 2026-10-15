using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ProxyNET.Configuration;
using Serilog;
using Serilog.Events;

namespace ProxyNET;

/// <summary>
/// Represents a session for a proxy connection, handling data forwarding between a peer and the target endpoint.
/// </summary>
public sealed class ProxySession : IDisposable
{
    private const long StatsFlushThreshold = 1024 * 1024;
    private const string PeerToTargetName = "Peer -> Target";
    private const string TargetToPeerName = "Target -> Peer";

    private readonly TcpClient _peer;
    private readonly EndPoint _forwardEndpoint;
    private readonly ForwardingConfig _config;
    private readonly ProxyStats _stats;
    private readonly CancellationToken _shutdownToken;
    private readonly CancellationTokenSource _cts;

    private long _lastActivityTicks = Environment.TickCount64;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxySession"/> class.
    /// </summary>
    /// <param name="peer">The TCP client representing the connected peer. Its lifetime is owned by the caller.</param>
    /// <param name="forwardEndpoint">The endpoint of the target the traffic is forwarded to.</param>
    /// <param name="config">The forwarding configuration providing the session timeouts and buffer sizes.</param>
    /// <param name="stats">The counters of the listener the peer connected to.</param>
    /// <param name="shutdownToken">A token that closes the session when cancelled.</param>
    public ProxySession(TcpClient peer, EndPoint forwardEndpoint, ForwardingConfig config, ProxyStats stats,
        CancellationToken shutdownToken)
    {
        _peer = peer;
        _forwardEndpoint = forwardEndpoint;
        _config = config;
        _stats = stats;
        _shutdownToken = shutdownToken;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
    }

    /// <summary>
    /// The time, in milliseconds, since the session last transferred data in either direction.
    /// </summary>
    public long IdleMilliseconds => Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks);

    /// <summary>
    /// Releases the cancellation source of the session. Linked sources keep a registration on the token they were
    /// created from, so a session that is not disposed would stay referenced for as long as the proxy runs.
    /// </summary>
    public void Dispose() => _cts.Dispose();

    /// <summary>
    /// Aborts a session that made no progress within the configured timeout.
    /// </summary>
    /// <param name="idleTimeout">The timeout that was exceeded, in milliseconds.</param>
    public void AbortIdle(int idleTimeout)
    {
        _stats.OnIdleTimeout();

        Log.Debug("Aborting a session that made no progress for {Idle} ms, the limit is {Timeout} ms",
            IdleMilliseconds, idleTimeout);

        Cancel();
    }

    /// <summary>
    /// Runs the proxy session, establishing a connection to the target and forwarding data in both directions until
    /// both of them are closed, the session is aborted or the proxy shuts down.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task RunAsync()
    {
        var traceConnections = Log.IsEnabled(LogEventLevel.Debug);
        var peerEndpoint = traceConnections ? _peer.Client.RemoteEndPoint : null;

        if (traceConnections)
            Log.Debug("Peer connected: {RemoteAddr} -> {LocalAddr}", peerEndpoint, _peer.Client.LocalEndPoint);

        using var target = new TcpClient();

        try
        {
            if (!await TryConnectToTargetAsync(target))
            {
                _stats.OnTargetUnreachable();

                return;
            }

            if (traceConnections)
                Log.Debug("Target connected: {LocalAddr} -> {RemoteAddr}", target.Client.LocalEndPoint,
                    target.Client.RemoteEndPoint);

            ConfigureSocket(_peer.Client);
            ConfigureSocket(target.Client);

            await using var peerStream = _peer.GetStream();
            await using var targetStream = target.GetStream();

            var toTargetTask = ForwardDataAsync(_peer, peerStream, target, targetStream, ProxyDirection.PeerToTarget);
            var toPeerTask = ForwardDataAsync(target, targetStream, _peer, peerStream, ProxyDirection.TargetToPeer);

            await Task.WhenAll(toTargetTask, toPeerTask);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during the proxy session with {RemoteAddr}", peerEndpoint);
        }
        finally
        {
            if (traceConnections)
                Log.Debug("Peer disconnected: {RemoteAddr}", peerEndpoint);
        }
    }

    /// <summary>
    /// Attempts to connect to the target endpoint. Host names are resolved here rather than at startup, so DNS
    /// changes are picked up and round-robin records spread across connections.
    /// </summary>
    /// <param name="target">The TCP client representing the connection to the target.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if connected successfully; otherwise, false.</returns>
    private async Task<bool> TryConnectToTargetAsync(TcpClient target)
    {
        _cts.CancelAfter(ForwardingConfig.ToTimeoutMilliseconds(_config.ConnectTimeout));

        try
        {
            switch (_forwardEndpoint)
            {
                case DnsEndPoint dns:
                    await target.ConnectAsync(dns.Host, dns.Port, _cts.Token);
                    break;
                case IPEndPoint ip:
                    await target.ConnectAsync(ip.Address, ip.Port, _cts.Token);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported forward endpoint type {_forwardEndpoint.GetType().Name}.");
            }

            _cts.CancelAfter(Timeout.Infinite);

            return true;
        }
        catch (OperationCanceledException) when (!_shutdownToken.IsCancellationRequested)
        {
            Log.Warning("Timed out after {Timeout} ms connecting to target at {TargetEndpoint}",
                _config.ConnectTimeout, _forwardEndpoint);

            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Connection to target at {TargetEndpoint} was cancelled", _forwardEndpoint);

            return false;
        }
        catch (SocketException ex)
        {
            Log.Warning("Failed to connect to target at {TargetEndpoint}. SocketErrorCode: {SocketErrorCode}",
                _forwardEndpoint, ex.SocketErrorCode);

            return false;
        }
    }

    /// <summary>
    /// Forwards data from the source to the destination until the source reaches the end of its stream, at which
    /// point the half-close is propagated to the destination and the opposite direction is left running so data
    /// still in flight is not truncated.
    /// </summary>
    /// <remarks>
    /// A buffer is only rented once the source has data to hand over, so sessions that sit idle hold none, and the
    /// memory of the proxy follows the number of active transfers rather than the number of connections. Progress
    /// is published as a timestamp the listener sweeps, which keeps the loop free of per transfer timers.
    /// </remarks>
    /// <param name="source">The connection to read data from.</param>
    /// <param name="sourceStream">The stream of <paramref name="source"/>.</param>
    /// <param name="destination">The connection to write data to.</param>
    /// <param name="destinationStream">The stream of <paramref name="destination"/>.</param>
    /// <param name="direction">The direction the data flows in.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ForwardDataAsync(TcpClient source, NetworkStream sourceStream, TcpClient destination,
        NetworkStream destinationStream, ProxyDirection direction)
    {
        var name = direction == ProxyDirection.PeerToTarget ? PeerToTargetName : TargetToPeerName;
        var sourceSocket = source.Client;
        var bufferSize = _config.BufferSize;
        var waitForData = _config.WaitForData;
        var writeTimeout = _config.WriteTimeout;
        var token = _cts.Token;
        var pendingBytes = 0L;

        try
        {
            while (true)
            {
                if (waitForData)
                    await sourceSocket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, token);

                var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bufferSize), token);

                    if (bytesRead == 0)
                    {
                        Log.Debug("{Direction}: connection closed by the source", name);

                        TryShutdownSend(destination, name);

                        return;
                    }

                    Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

                    if (writeTimeout > 0)
                        await WriteWithTimeoutAsync(destinationStream, buffer, bytesRead, writeTimeout, token);
                    else
                        await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);

                    Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

                    pendingBytes += bytesRead;

                    if (pendingBytes >= StatsFlushThreshold)
                    {
                        _stats.AddBytes(direction, pendingBytes);

                        pendingBytes = 0;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("{Direction}: forwarding was cancelled", name);
        }
        catch (TimeoutException ex)
        {
            Log.Debug("{Direction}: {Reason}", name, ex.Message);

            Cancel();
        }
        catch (SocketException ex)
        {
            Log.Debug("{Direction}: SocketException occurred. ErrorCode: {ErrorCode}", name, ex.SocketErrorCode);

            Cancel();
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException socketEx)
        {
            Log.Debug("{Direction}: SocketException occurred. ErrorCode: {ErrorCode}", name,
                socketEx.SocketErrorCode);

            Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Direction}: Unexpected error while forwarding data", name);

            Cancel();
        }
        finally
        {
            if (pendingBytes > 0)
                _stats.AddBytes(direction, pendingBytes);
        }
    }

    /// <summary>
    /// Writes a chunk, aborting it when it takes longer than the configured write timeout.
    /// </summary>
    /// <param name="destinationStream">The stream to write to.</param>
    /// <param name="buffer">The buffer holding the chunk.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="writeTimeout">The write timeout in milliseconds.</param>
    /// <param name="token">The cancellation token of the session.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="TimeoutException">The write did not complete within the timeout.</exception>
    private static async ValueTask WriteWithTimeoutAsync(NetworkStream destinationStream, byte[] buffer, int count,
        int writeTimeout, CancellationToken token)
    {
        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        writeCts.CancelAfter(writeTimeout);

        try
        {
            await destinationStream.WriteAsync(buffer.AsMemory(0, count), writeCts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"a write of {count} bytes did not complete within {writeTimeout} ms");
        }
    }

    /// <summary>
    /// Applies the configured socket options to one side of the session.
    /// </summary>
    /// <param name="socket">The socket to configure.</param>
    private void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = _config.NoDelay;

        if (_config.ReceiveBufferSize > 0)
            socket.ReceiveBufferSize = _config.ReceiveBufferSize;

        if (_config.SendBufferSize > 0)
            socket.SendBufferSize = _config.SendBufferSize;
    }

    /// <summary>
    /// Stops both directions of the session.
    /// </summary>
    private void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    /// <summary>
    /// Propagates a half-close by shutting down the sending side of the destination connection.
    /// </summary>
    /// <param name="destination">The connection whose sending side should be shut down.</param>
    /// <param name="direction">The name of the direction, used for logging.</param>
    private static void TryShutdownSend(TcpClient destination, string direction)
    {
        try
        {
            destination.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Log.Debug("{Direction}: could not shut down the destination for sending. {Reason}", direction, ex.Message);
        }
    }
}
