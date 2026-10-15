using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using ProxyNET;
using ProxyNET.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

const string configFileName = "appsettings.json";
const int defaultStatsInterval = 60;

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

    var statsInterval = configuration.GetValue("StatsInterval", defaultStatsInterval);

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

    foreach (var config in forwardingConfigs.Where(config => config.UsesLegacyReadTimeout))
        Log.Warning("ReadTimeout on {Address}:{Port} is deprecated, use IdleTimeout instead", config.Address,
            config.Port);

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

        var statsTask = ReportStatsAsync(servers, statsInterval, cts.Token);

        await Task.WhenAll(servers.Select(server => server.RunAsync(cts.Token)));

        await cts.CancelAsync();
        await statsTask;

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

static async Task ReportStatsAsync(List<ProxyServer> servers, int intervalSeconds, CancellationToken cancellationToken)
{
    if (intervalSeconds <= 0)
        return;

    var interval = TimeSpan.FromSeconds(intervalSeconds);

    using var timer = new PeriodicTimer(interval);

    var previous = servers.Select(server => server.Stats.Snapshot()).ToArray();

    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
            for (var index = 0; index < servers.Count; index++)
            {
                var current = servers[index].Stats.Snapshot();
                var baseline = previous[index];

                previous[index] = current;

                Log.Information(
                    "{Endpoint}: {Active} active, {Accepted} accepted (+{NewSessions}), in {InboundRate}, out {OutboundRate}, {Rejected} rejected, {Failed} unreachable, {IdleTimeouts} idle aborts",
                    servers[index].BoundEndpoint, current.Active, current.Accepted,
                    current.Accepted - baseline.Accepted,
                    FormatRate(current.BytesToTarget - baseline.BytesToTarget, interval),
                    FormatRate(current.BytesToPeer - baseline.BytesToPeer, interval),
                    current.Rejected, current.Failed, current.IdleTimeouts);
            }
    }
    catch (OperationCanceledException)
    {
        return;
    }
}

static string FormatRate(long bytes, TimeSpan interval)
{
    var perSecond = bytes / interval.TotalSeconds;

    return perSecond switch
    {
        >= 1024d * 1024 * 1024 => $"{perSecond / (1024d * 1024 * 1024):F2} GB/s",
        >= 1024d * 1024 => $"{perSecond / (1024d * 1024):F2} MB/s",
        >= 1024d => $"{perSecond / 1024d:F2} KB/s",
        _ => $"{perSecond:F0} B/s"
    };
}

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
