using System.Net;
using System.Net.Sockets;
using ProxyNET.Configuration;
using Serilog;

namespace ProxyNET;

public class ProxyServer(ForwardingConfig config)
{
    public async Task StartAsync()
    {
        try
        {
            var listenEndpoint = new IPEndPoint(IPAddress.Parse(config.Address), config.Port);
            var forwardEndpoint = new IPEndPoint(IPAddress.Parse(config.Forward.Address), config.Forward.Port);

            var listener = new TcpListener(listenEndpoint);
            listener.Start();

            Log.Information("Proxy on {ListenEndpoint}, forwarding to {ForwardEndpoint}", listenEndpoint,
                forwardEndpoint);

            while (true)
            {
                var peer = await listener.AcceptTcpClientAsync();

                _ = HandleClientAsync(peer, forwardEndpoint);
            }
        }
        catch (Exception)
        {
            Log.Error("Could not start the proxy {Proxy}:{Port}", config.Address, config.Port);
        }
    }

    private static async Task HandleClientAsync(TcpClient peer, IPEndPoint forwardEndpoint)
    {
        try
        {
            var session = new ProxySession(peer, forwardEndpoint);

            await session.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encountered an exception");
        }
        finally
        {
            peer.Dispose();
        }
    }
}