using Microsoft.Extensions.Configuration;
using ProxyNET;
using ProxyNET.Configuration;
using Serilog;

Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json");

var configuration = builder.Build();

var forwardingConfigs = configuration.GetSection("Forwarding").Get<List<ForwardingConfig>>();

if (forwardingConfigs is null or []) return;

foreach (var proxyServer in forwardingConfigs.Select(forwardConfig => new ProxyServer(forwardConfig)))
    await Task.Run(async () => { await proxyServer.StartAsync(); });

await Task.Delay(-1);