using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Serilog;

namespace ProxyNET;

/// <summary>
/// Represents a session for a proxy connection, handling data forwarding between a client and a proxy server.
/// </summary>
/// <param name="peer">The TCP client representing the connected client.</param>
/// <param name="proxyEndpoint">The endpoint of the proxy server to connect to.</param>
/// <param name="readTimeout">The timeout duration for reading data from the proxy server, in milliseconds.</param>
/// <param name="writeTimeout">The timeout duration for writing data to the proxy server, in milliseconds.</param>
public class ProxySession(TcpClient peer, IPEndPoint proxyEndpoint, int readTimeout, int writeTimeout)
{
    private const int BufferSize = 8192;
    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    /// <summary>
    /// Runs the proxy session, establishing a connection to the proxy server and forwarding data between the client and the proxy.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task RunAsync()
    {
        Log.Information("Peer connected: {RemoteAddr} -> {LocalAddr}", peer.Client.RemoteEndPoint, peer.Client.LocalEndPoint);

        using var proxy = new TcpClient();

        try
        {
            if (!await TryConnectToProxyAsync(proxy, proxyEndpoint))
            {
                Log.Error("Failed to connect to proxy at {ProxyEndpoint}", proxyEndpoint);
                return;
            }

            Log.Information("Proxy connected: {LocalAddr} -> {RemoteAddr}", proxy.Client.LocalEndPoint, proxy.Client.RemoteEndPoint);

            await using var peerStream = peer.GetStream();
            await using var proxyStream = proxy.GetStream();

            ConfigureStreamTimeouts(peerStream, readTimeout, writeTimeout);
            ConfigureStreamTimeouts(proxyStream, readTimeout, writeTimeout);

            var peerToProxyTask = ForwardDataAsync(peerStream, proxyStream, "Peer -> Proxy");
            var proxyToPeerTask = ForwardDataAsync(proxyStream, peerStream, "Proxy -> Peer");

            // Wait for either of the tasks to complete
            await Task.WhenAny(peerToProxyTask, proxyToPeerTask);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred during the proxy session");
        }
    }

    /// <summary>
    /// Attempts to connect to the proxy server.
    /// </summary>
    /// <param name="proxy">The TCP client representing the proxy connection.</param>
    /// <param name="proxyEndpoint">The endpoint of the proxy server.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if connected successfully; otherwise, false.</returns>
    private static async Task<bool> TryConnectToProxyAsync(TcpClient proxy, IPEndPoint proxyEndpoint)
    {
        try
        {
            await proxy.ConnectAsync(proxyEndpoint.Address, proxyEndpoint.Port);
            
            return true;
        }
        catch (SocketException ex)
        {
            Log.Warning(ex, "Failed to connect to proxy at {ProxyEndpoint}. SocketErrorCode: {SocketErrorCode}", proxyEndpoint, ex.SocketErrorCode);
            
            return false;
        }
    }

    /// <summary>
    /// Configures timeouts for a network stream.
    /// </summary>
    /// <param name="stream">The network stream to configure.</param>
    /// <param name="readTimeout">Read timeout in milliseconds.</param>
    /// <param name="writeTimeout">Write timeout in milliseconds.</param>
    private static void ConfigureStreamTimeouts(NetworkStream stream, int readTimeout, int writeTimeout)
    {
        stream.ReadTimeout = readTimeout;
        stream.WriteTimeout = writeTimeout;
    }

    /// <summary>
    /// Forwards data from the source network stream to the destination network stream.
    /// </summary>
    /// <param name="source">The source network stream to read data from.</param>
    /// <param name="destination">The destination network stream to write data to.</param>
    /// <param name="direction">A string indicating the direction of data flow (e.g., "Peer -> Proxy").</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static async Task ForwardDataAsync(NetworkStream source, NetworkStream destination, string direction)
    {
        var buffer = BufferPool.Rent(BufferSize);

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, BufferSize));
                
                if (bytesRead <= 0)
                {
                    Log.Information("{Direction}: Connection closed by the source", direction);
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead));
                
                Log.Debug("{Direction}: Forwarded {Bytes} bytes", direction, bytesRead);
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException socketEx)
        {
            Log.Warning(socketEx, "{Direction}: SocketException occurred. ErrorCode: {ErrorCode}", direction, socketEx.SocketErrorCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Direction}: Unexpected error while forwarding data", direction);
        }
        finally
        {
            BufferPool.Return(buffer);
        }
    }
}
