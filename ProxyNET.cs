using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
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

var listenEndpoint = new IPEndPoint(addr, port);

var listener = new TcpListener(listenEndpoint);

Console.WriteLine($"Listening on port {port}");