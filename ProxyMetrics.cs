using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ProxyNET;

/// <summary>
/// Publishes the counters of every listener through <see cref="System.Diagnostics.Metrics"/>, which costs nothing
/// while no collector is attached and can be read live with <c>dotnet-counters</c> or an OpenTelemetry exporter.
/// </summary>
public static class ProxyMetrics
{
    /// <summary>
    /// The name of the meter to subscribe to.
    /// </summary>
    public const string MeterName = "ProxyNET";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ConcurrentDictionary<string, ProxyStats> Listeners = new();

    static ProxyMetrics()
    {
        Meter.CreateObservableGauge("proxynet.sessions.active", () => Observe(static stats => stats.Active),
            "{session}", "Sessions currently being forwarded.");

        Meter.CreateObservableCounter("proxynet.sessions.accepted",
            () => Observe(static stats => stats.Snapshot().Accepted), "{session}", "Connections accepted.");

        Meter.CreateObservableCounter("proxynet.sessions.rejected",
            () => Observe(static stats => stats.Snapshot().Rejected), "{session}",
            "Connections closed immediately because the session limit was reached.");

        Meter.CreateObservableCounter("proxynet.sessions.failed",
            () => Observe(static stats => stats.Snapshot().Failed), "{session}",
            "Sessions that could not reach the target.");

        Meter.CreateObservableCounter("proxynet.sessions.idle_timeouts",
            () => Observe(static stats => stats.Snapshot().IdleTimeouts), "{session}",
            "Sessions aborted for making no progress.");

        Meter.CreateObservableCounter("proxynet.bytes.to_target",
            () => Observe(static stats => stats.Snapshot().BytesToTarget), "By",
            "Bytes forwarded from the peers to the target.");

        Meter.CreateObservableCounter("proxynet.bytes.to_peer",
            () => Observe(static stats => stats.Snapshot().BytesToPeer), "By",
            "Bytes forwarded from the target back to the peers.");
    }

    /// <summary>
    /// Starts publishing the counters of a listener.
    /// </summary>
    /// <param name="listener">The endpoint the listener is bound to, used as the metric tag.</param>
    /// <param name="stats">The counters to publish.</param>
    public static void Register(string listener, ProxyStats stats) => Listeners[listener] = stats;

    /// <summary>
    /// Stops publishing the counters of a listener.
    /// </summary>
    /// <param name="listener">The endpoint the listener was bound to.</param>
    public static void Unregister(string listener) => Listeners.TryRemove(listener, out _);

    private static IEnumerable<Measurement<long>> Observe(Func<ProxyStats, long> selector) =>
        Listeners.Select(entry =>
            new Measurement<long>(selector(entry.Value), new KeyValuePair<string, object?>("listener", entry.Key)));
}
