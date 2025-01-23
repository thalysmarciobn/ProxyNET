using Microsoft.Extensions.Configuration;
using ProxyNET;
using ProxyNET.Configuration;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .Build();
    
    var forwardingConfigs = configuration.GetSection("Forwarding").Get<List<ForwardingConfig>>();

    if (forwardingConfigs is null || forwardingConfigs.Count == 0)
    {
        Log.Warning("No forwarding configurations found.");
        return;
    }
    
    var tasks = forwardingConfigs.Select(async config =>
    {
        try
        {
            var proxyServer = new ProxyServer(config);
            
            await proxyServer.StartAsync();
            
            Log.Information("Proxy started on {Address}:{Port}", config.Address, config.Port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start proxy on {Address}:{Port}", config.Address, config.Port);
        }
    });

    await Task.WhenAll(tasks);
    
    await Task.Delay(Timeout.Infinite);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unexpected error in the application");
}
finally
{
    Log.CloseAndFlush();
}