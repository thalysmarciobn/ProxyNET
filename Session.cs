using System.Net;
using System.Net.Sockets;
using Serilog;

namespace ProxyNET;

public class Session(TcpClient peer, IPEndPoint proxyEndpoint)
{
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
}