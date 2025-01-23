using System.Net;
using System.Net.Sockets;
using ProxyNET.Configuration;
using Serilog;

namespace ProxyNET;

/// <summary>
/// Represents a proxy server that listens for connections and forwards them to a target endpoint.
/// </summary>
/// <param name="config">The forwarding configuration containing the proxy and target address and port.</param>
public class ProxyServer(ForwardingConfig config)
{
    /// <summary>
    /// Starts the proxy server and begins listening for client connections.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task StartAsync()
    {
        try
        {
            var listenEndpoint = new IPEndPoint(IPAddress.Parse(config.Address), config.Port);
            var forwardEndpoint = new IPEndPoint(IPAddress.Parse(config.Forward.Address), config.Forward.Port);

            using var listener = new TcpListener(listenEndpoint);
            listener.Start();

            Log.Information("Proxy started on {ListenEndpoint}, forwarding to {ForwardEndpoint}", listenEndpoint, forwardEndpoint);

            while (true)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();

                    _ = Task.Run(() => HandleClientAsync(client, forwardEndpoint));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error accepting client connection.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to start the proxy on {Address}:{Port}", config.Address, config.Port);
        }
    }

    /// <summary>
    /// Handles a client connection by creating a proxy session and running it.
    /// </summary>
    /// <param name="peer">The TCP client representing the connected client.</param>
    /// <param name="forwardEndpoint">The endpoint to which the connection should be forwarded.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task HandleClientAsync(TcpClient peer, IPEndPoint forwardEndpoint)
    {
        try
        {
            var session = new ProxySession(peer, forwardEndpoint, config.ReadTimeout, config.WriteTimeout);

            await session.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encountered an exception");
        }
    }
}