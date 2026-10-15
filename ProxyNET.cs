using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using ProxyNET;
using ProxyNET.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

const string configFileName = "appsettings.json";

var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var configPath = Path.Combine(AppContext.BaseDirectory, configFileName);

    if (!File.Exists(configPath))
    {
        Log.Fatal("Configuration file not found at {ConfigPath}", configPath);

        return 1;
    }

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(configFileName, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables("PROXYNET_")
        .Build();

    if (Enum.TryParse<LogEventLevel>(configuration["LogLevel"], ignoreCase: true, out var configuredLevel))
        levelSwitch.MinimumLevel = configuredLevel;

    List<ForwardingConfig>? forwardingConfigs;

    try
    {
        forwardingConfigs = configuration.GetSection("Forwarding").Get<List<ForwardingConfig>>();
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "The Forwarding section of {ConfigPath} could not be read", configPath);

        return 1;
    }

    if (forwardingConfigs is null || forwardingConfigs.Count == 0)
    {
        Log.Fatal("No forwarding configurations found in {ConfigPath}", configPath);

        return 1;
    }

    var errors = forwardingConfigs.SelectMany((config, index) => config.Validate(index)).ToList();

    errors.AddRange(FindDuplicateListenEndpoints(forwardingConfigs));

    if (errors.Count > 0)
    {
        foreach (var error in errors)
            Log.Fatal("Invalid configuration. {Error}", error);

        return 1;
    }

    using var cts = new CancellationTokenSource();
    using var sigTermRegistration = RequestShutdownOn(cts);

    var servers = new List<ProxyServer>(forwardingConfigs.Count);

    try
    {
        foreach (var config in forwardingConfigs)
        {
            var server = new ProxyServer(config);

            try
            {
                server.Start();

                servers.Add(server);
            }
            catch (Exception ex)
            {
                server.Dispose();

                Log.Error(ex, "Failed to start proxy on {Address}:{Port}", config.Address, config.Port);
            }
        }

        if (servers.Count == 0)
        {
            Log.Fatal("None of the {Total} configured proxies could be started", forwardingConfigs.Count);

            return 1;
        }

        Log.Information("{Started} of {Total} proxies running. Press Ctrl+C to stop.", servers.Count,
            forwardingConfigs.Count);

        await Task.WhenAll(servers.Select(server => server.AcceptAsync(cts.Token)));

        Log.Information("Shutting down");

        await Task.WhenAll(servers.Select(server => server.StopAsync(TimeSpan.FromSeconds(5))));

        return servers.Count == forwardingConfigs.Count ? 0 : 1;
    }
    finally
    {
        foreach (var server in servers)
            server.Dispose();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unexpected error in the application");

    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static IEnumerable<string> FindDuplicateListenEndpoints(List<ForwardingConfig> configs) =>
    configs
        .Where(config => config.Port != 0)
        .GroupBy(config => $"{config.Address.Trim()}:{config.Port}", StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => $"Forwarding: {group.Key} is configured {group.Count()} times.");

static PosixSignalRegistration RequestShutdownOn(CancellationTokenSource cts)
{
    void Cancel(string signal)
    {
        Log.Information("Received {Signal}, shutting down", signal);

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    Console.CancelKeyPress += (_, args) =>
    {
        args.Cancel = true;

        Cancel("Ctrl+C");
    };

    return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;

        Cancel("SIGTERM");
    });
}
