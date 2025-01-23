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

        await using var peerNetStream = peer.GetStream();
    }
}