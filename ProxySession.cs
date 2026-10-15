using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ProxyNET.Configuration;
using Serilog;

namespace ProxyNET;

/// <summary>
/// Represents a session for a proxy connection, handling data forwarding between a peer and the target endpoint.
/// </summary>
/// <param name="peer">The TCP client representing the connected peer. Its lifetime is owned by the caller.</param>
/// <param name="forwardEndpoint">The endpoint of the target the traffic is forwarded to.</param>
/// <param name="config">The forwarding configuration providing the session timeouts and buffer size.</param>
public class ProxySession(TcpClient peer, EndPoint forwardEndpoint, ForwardingConfig config)
{
    private const string ReadOperation = "read";
    private const string WriteOperation = "write";

    /// <summary>
    /// Runs the proxy session, establishing a connection to the target and forwarding data in both directions
    /// until both of them are closed, the session times out or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">A token that closes the session when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var peerEndpoint = peer.Client.RemoteEndPoint;

        Log.Information("Peer connected: {RemoteAddr} -> {LocalAddr}", peerEndpoint, peer.Client.LocalEndPoint);

        using var proxy = new TcpClient();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            if (!await TryConnectToProxyAsync(proxy, forwardEndpoint, config.ConnectTimeout, sessionCts.Token))
                return;

            Log.Information("Proxy connected: {LocalAddr} -> {RemoteAddr}", proxy.Client.LocalEndPoint,
                proxy.Client.RemoteEndPoint);

            peer.NoDelay = true;
            proxy.NoDelay = true;

            await using var peerStream = peer.GetStream();
            await using var proxyStream = proxy.GetStream();

            var peerToProxyTask = ForwardDataAsync(peerStream, proxyStream, proxy, "Peer -> Proxy", sessionCts);
            var proxyToPeerTask = ForwardDataAsync(proxyStream, peerStream, peer, "Proxy -> Peer", sessionCts);

            await Task.WhenAll(peerToProxyTask, proxyToPeerTask);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during the proxy session with {RemoteAddr}", peerEndpoint);
        }
        finally
        {
            Log.Information("Peer disconnected: {RemoteAddr}", peerEndpoint);
        }
    }

    /// <summary>
    /// Attempts to connect to the target endpoint. Host names are resolved here rather than at startup, so DNS
    /// changes are picked up and round-robin records spread across connections.
    /// </summary>
    /// <param name="proxy">The TCP client representing the connection to the target.</param>
    /// <param name="endpoint">The endpoint of the target.</param>
    /// <param name="connectTimeout">The connect timeout in milliseconds, or <c>0</c> when disabled.</param>
    /// <param name="cancellationToken">A token that aborts the attempt when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if connected successfully; otherwise, false.</returns>
    private static async Task<bool> TryConnectToProxyAsync(TcpClient proxy, EndPoint endpoint, int connectTimeout,
        CancellationToken cancellationToken)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        connectCts.CancelAfter(ForwardingConfig.ToTimeoutMilliseconds(connectTimeout));

        try
        {
            switch (endpoint)
            {
                case DnsEndPoint dns:
                    await proxy.ConnectAsync(dns.Host, dns.Port, connectCts.Token);
                    break;
                case IPEndPoint ip:
                    await proxy.ConnectAsync(ip.Address, ip.Port, connectCts.Token);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported forward endpoint type {endpoint.GetType().Name}.");
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Timed out after {Timeout} ms connecting to proxy at {ProxyEndpoint}", connectTimeout,
                endpoint);

            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Connection to proxy at {ProxyEndpoint} was cancelled", endpoint);

            return false;
        }
        catch (SocketException ex)
        {
            Log.Warning("Failed to connect to proxy at {ProxyEndpoint}. SocketErrorCode: {SocketErrorCode}", endpoint,
                ex.SocketErrorCode);

            return false;
        }
    }

    /// <summary>
    /// Forwards data from the source stream to the destination stream until the source reaches the end of its
    /// stream, at which point the half-close is propagated to the destination and the opposite direction is left
    /// running so data still in flight is not truncated.
    /// </summary>
    /// <remarks>
    /// <see cref="NetworkStream.ReadTimeout"/> and <see cref="NetworkStream.WriteTimeout"/> only apply to the
    /// synchronous overloads, so the configured timeouts are enforced through cancellation. A single linked source
    /// is reused for every operation: its deadline is armed before the operation and disarmed once it completed.
    /// </remarks>
    /// <param name="sourceStream">The stream to read data from.</param>
    /// <param name="destinationStream">The stream to write data to.</param>
    /// <param name="destination">The connection behind <paramref name="destinationStream"/>, used to propagate the half-close.</param>
    /// <param name="direction">A string indicating the direction of data flow (e.g., "Peer -> Proxy").</param>
    /// <param name="sessionCts">The session wide cancellation source, cancelled when this direction fails.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ForwardDataAsync(NetworkStream sourceStream, NetworkStream destinationStream,
        TcpClient destination, string direction, CancellationTokenSource sessionCts)
    {
        var readTimeout = ForwardingConfig.ToTimeoutMilliseconds(config.ReadTimeout);
        var writeTimeout = ForwardingConfig.ToTimeoutMilliseconds(config.WriteTimeout);
        var bufferSize = config.BufferSize;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
        var pendingOperation = ReadOperation;

        try
        {
            while (true)
            {
                pendingOperation = ReadOperation;
                operationCts.CancelAfter(readTimeout);

                var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bufferSize), operationCts.Token);

                operationCts.CancelAfter(Timeout.Infinite);

                if (bytesRead == 0)
                {
                    Log.Debug("{Direction}: connection closed by the source", direction);

                    TryShutdownSend(destination, direction);

                    return;
                }

                pendingOperation = WriteOperation;
                operationCts.CancelAfter(writeTimeout);

                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), operationCts.Token);

                operationCts.CancelAfter(Timeout.Infinite);

                Log.Debug("{Direction}: forwarded {Bytes} bytes", direction, bytesRead);
            }
        }
        catch (OperationCanceledException) when (!sessionCts.IsCancellationRequested)
        {
            var timeout = pendingOperation == ReadOperation ? config.ReadTimeout : config.WriteTimeout;

            Log.Warning("{Direction}: {Operation} timed out after {Timeout} ms", direction, pendingOperation, timeout);

            sessionCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            Log.Debug("{Direction}: forwarding was cancelled", direction);
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException socketEx)
        {
            Log.Warning("{Direction}: SocketException occurred. ErrorCode: {ErrorCode}", direction,
                socketEx.SocketErrorCode);

            sessionCts.Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Direction}: Unexpected error while forwarding data", direction);

            sessionCts.Cancel();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Propagates a half-close by shutting down the sending side of the destination connection.
    /// </summary>
    /// <param name="destination">The connection whose sending side should be shut down.</param>
    /// <param name="direction">A string indicating the direction of data flow, used for logging.</param>
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
