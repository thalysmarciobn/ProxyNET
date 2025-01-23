using System.Net;
using System.Net.Sockets;
using Serilog;

namespace ProxyNET;

public class Session(TcpClient peer, IPEndPoint proxyEndpoint)
{
    private const int BufferSize = 8192;

    public async Task RunAsync()
    {
        Log.Information("Peer connected: {RemoteAddr} -> {LocalAddr}", peer.Client.RemoteEndPoint,
            peer.Client.LocalEndPoint);

        var proxy = new TcpClient();

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

        var peerToProxy = ForwardDataAsync(peerNetStream, proxyNetStream, "Peer -> Proxy");
        var proxyToPeer = ForwardDataAsync(proxyNetStream, peerNetStream, "Proxy -> Peer");

        await Task.WhenAny(peerToProxy, proxyToPeer);
    }

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

    private static async Task ForwardDataAsync(NetworkStream source, NetworkStream destination, string direction)
    {
        var buffer = new byte[BufferSize];

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer);

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
    }
}