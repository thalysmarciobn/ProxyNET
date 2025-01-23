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
        Log.Information("Peer connected: {RemoteAddr} -> {LocalAddr}", peer.Client.RemoteEndPoint,
            peer.Client.LocalEndPoint);

        var proxy = new TcpClient();
        
        try
        {
            var isConnected = await ProxyConnectedAsync(proxy, proxyEndpoint);

            if (!isConnected)
            {
                Log.Error("Could not connect to proxy");

                return;
            }

            Log.Information("Proxy connected: {LocalAddr} -> {RemoteAddr}", proxy.Client.LocalEndPoint,
                proxy.Client.RemoteEndPoint);

            await using var peerNetStream = peer.GetStream();
            await using var proxyNetStream = proxy.GetStream();
            
            peerNetStream.WriteTimeout = writeTimeout;
            peerNetStream.ReadTimeout = readTimeout;
            
            proxyNetStream.WriteTimeout = writeTimeout;
            proxyNetStream.ReadTimeout = readTimeout;

            var peerToProxy = ForwardDataAsync(peerNetStream, proxyNetStream, "Peer -> Proxy");
            var proxyToPeer = ForwardDataAsync(proxyNetStream, peerNetStream, "Proxy -> Peer");

            await Task.WhenAny(peerToProxy, proxyToPeer);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Error processing connection");
        }
        finally
        {
            proxy.Dispose();
        }
    }

    /// <summary>
    /// Attempts to connect to the proxy server.
    /// </summary>
    /// <param name="proxy">The TCP client representing the proxy connection.</param>
    /// <param name="proxyEndpoint">The endpoint of the proxy server.</param>
    /// <returns>A task that represents the asynchronous operation, returning true if connected successfully; otherwise, false.</returns>
    private static async Task<bool> ProxyConnectedAsync(TcpClient proxy, IPEndPoint proxyEndpoint)
    {
        try
        {
            await proxy.ConnectAsync(proxyEndpoint.Address, proxyEndpoint.Port);

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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
                    Log.Information("{Direction}: Connection closed", direction);

                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead));

                Log.Debug("{Direction}: Forwarded {NumBytes} bytes", direction, bytesRead);
            }
        }
        catch (SocketException ex)
        {
            Log.Error(ex, "{Direction}: SocketException occurred. SocketErrorCode: {SocketErrorCode}", direction,
                ex.SocketErrorCode);
            if (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Shutdown)
                Log.Warning("{Direction}: Socket operation was aborted or connection shut down", direction);
        }
        finally
        {
            BufferPool.Return(buffer);
        }
    }
}