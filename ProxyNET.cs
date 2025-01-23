using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using ProxyNET;
using Serilog;

Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json");

var configuration = builder.Build();

var addr = IPAddress.Parse(configuration.GetValue<string>("Address")!);
var port = configuration.GetValue<int>("Port");

var forwardConfig = configuration.GetSection("Forward");

var forwardAddr = IPAddress.Parse(forwardConfig.GetValue<string>("Address")!);
var forwardPort = forwardConfig.GetValue<int>("Port");

var listenEndpoint = new IPEndPoint(addr, port);
var forwardEndpoint = new IPEndPoint(forwardAddr, forwardPort);

var listener = new TcpListener(listenEndpoint);

listener.Start();

Log.Information("Proxy on {ListenEndpoint}, forwarding to {ForwardEndpoint}", listenEndpoint, forwardEndpoint);

while (true)
{
    var peer = await listener.AcceptTcpClientAsync();

    _ = Task.Run(async () =>
    {
        try
        {
            var session = new Session(peer, forwardEndpoint);

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
    });
}