using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ProxyNET.Configuration;
using Serilog;

namespace ProxyNET;

/// <summary>
/// Represents a proxy server that listens for connections and forwards them to a target endpoint.
/// </summary>
public sealed class ProxyServer : IDisposable
{
    private const int AcceptBackoffMilliseconds = 100;
    private const int DrainPollMilliseconds = 50;
    private const int MinSweepMilliseconds = 250;
    private const int MaxSweepMilliseconds = 30_000;

    private readonly ForwardingConfig _config;
    private readonly IPEndPoint _listenEndpoint;
    private readonly EndPoint _forwardEndpoint;
    private readonly ConcurrentDictionary<long, ProxySession> _sessions = new();
    private readonly ProxyStats _stats = new();

    private TcpListener? _listener;
    private string _name;
    private long _nextSessionId;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyServer"/> class.
    /// </summary>
    /// <param name="config">The forwarding configuration containing the proxy and target address and port.</param>
    /// <exception cref="ArgumentException">The configuration contains an address that cannot be parsed.</exception>
    public ProxyServer(ForwardingConfig config)
    {
        _config = config;

        if (!EndpointParser.TryParseListenAddress(config.Address, out var listenAddress))
            throw new ArgumentException($"Invalid listen address {config.Address}.", nameof(config));

        _listenEndpoint = new IPEndPoint(listenAddress, config.Port);
        _forwardEndpoint = EndpointParser.ParseForwardEndpoint(config.Forward.Address, config.Forward.Port);
        _name = _listenEndpoint.ToString();
    }

    /// <summary>
    /// The endpoint the listener is actually bound to, available once <see cref="Start"/> succeeded. It differs
    /// from the configured one when port <c>0</c> was requested.
    /// </summary>
    public IPEndPoint? BoundEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>
    /// The counters of this listener.
    /// </summary>
    public ProxyStats Stats => _stats;

    /// <summary>
    /// The number of sessions currently being forwarded.
    /// </summary>
    public long ActiveSessions => _stats.Active;

    /// <summary>
    /// Releases the listening socket and stops publishing the counters of this listener.
    /// </summary>
    public void Dispose()
    {
        ProxyMetrics.Unregister(_name);

        _listener?.Dispose();
    }

    /// <summary>
    /// Binds the listening socket. Failures are surfaced to the caller so a misconfigured listener is reported at
    /// startup instead of silently never accepting anything.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server was already started.</exception>
    /// <exception cref="SocketException">The endpoint could not be bound.</exception>
    public void Start()
    {
        if (_listener is not null)
            throw new InvalidOperationException("The proxy server was already started.");

        var listener = new TcpListener(_listenEndpoint);

        listener.Start();

        _listener = listener;
        _name = listener.LocalEndpoint.ToString()!;

        ProxyMetrics.Register(_name, _stats);

        Log.Information("Proxy listening on {ListenEndpoint}, forwarding to {ForwardEndpoint}", _name,
            _forwardEndpoint);
    }

    /// <summary>
    /// Accepts and forwards connections until <paramref name="cancellationToken"/> is cancelled or the listener can
    /// no longer be used.
    /// </summary>
    /// <param name="cancellationToken">A token that stops the listener and the running sessions when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Start"/> has not been called.</exception>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = _listener ??
                       throw new InvalidOperationException("Start must be called before accepting connections.");

        return Task.WhenAll(AcceptLoopAsync(listener, cancellationToken), SweepIdleSessionsAsync(cancellationToken));
    }

    /// <summary>
    /// Stops listening and waits for the running sessions to finish.
    /// </summary>
    /// <param name="drainTimeout">How long to wait for the running sessions before giving up.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task StopAsync(TimeSpan drainTimeout)
    {
        _listener?.Stop();

        if (_stats.Active == 0)
            return;

        Log.Information("Waiting for {Count} active session(s) on {Endpoint} to finish", _stats.Active, _name);

        var deadline = Environment.TickCount64 + (long)drainTimeout.TotalMilliseconds;

        while (_stats.Active > 0 && Environment.TickCount64 < deadline)
            await Task.Delay(DrainPollMilliseconds, CancellationToken.None);

        if (_stats.Active > 0)
            Log.Warning("{Count} session(s) on {Endpoint} did not finish within {Timeout}", _stats.Active, _name,
                drainTimeout);
    }

    /// <summary>
    /// Accepts connections, rejecting them when the session limit is reached. Failures that leave the listener
    /// unusable end the loop instead of turning it into a busy error loop.
    /// </summary>
    /// <param name="listener">The bound listener.</param>
    /// <param name="cancellationToken">A token that stops the loop when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient peer;

            try
            {
                peer = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (IsRetryableAcceptError(ex.SocketErrorCode))
            {
                Log.Warning("Error accepting a connection on {Endpoint}. SocketErrorCode: {SocketErrorCode}", _name,
                    ex.SocketErrorCode);

                if (IsResourceExhaustion(ex.SocketErrorCode))
                    await Task.Delay(AcceptBackoffMilliseconds, CancellationToken.None);

                continue;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Stopped accepting connections on {Endpoint}", _name);

                break;
            }

            if (!_stats.TryEnterSession(_config.MaxConnections))
            {
                Log.Debug("Rejected a connection on {Endpoint}, the limit of {MaxConnections} sessions is reached",
                    _name, _config.MaxConnections);

                peer.Dispose();

                continue;
            }

            _ = HandleClientAsync(peer, cancellationToken);
        }

        Log.Information("Proxy on {Endpoint} stopped accepting connections", _name);
    }

    /// <summary>
    /// Aborts the sessions that make no progress, replacing the timer every transfer would otherwise need with a
    /// single pass over the sessions of this listener.
    /// </summary>
    /// <param name="cancellationToken">A token that stops the sweep when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task SweepIdleSessionsAsync(CancellationToken cancellationToken)
    {
        var idleTimeout = _config.EffectiveIdleTimeout;

        if (idleTimeout <= 0)
            return;

        var interval = TimeSpan.FromMilliseconds(Math.Clamp(idleTimeout / 4, MinSweepMilliseconds,
            MaxSweepMilliseconds));

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                foreach (var entry in _sessions)
                    if (entry.Value.IdleMilliseconds >= idleTimeout)
                        entry.Value.AbortIdle(idleTimeout);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    /// <summary>
    /// Handles a client connection by creating a proxy session and running it. The session borrows the connection,
    /// which is closed here once the session ended.
    /// </summary>
    /// <param name="peer">The TCP client representing the connected client.</param>
    /// <param name="cancellationToken">A token that closes the session when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task HandleClientAsync(TcpClient peer, CancellationToken cancellationToken)
    {
        using var client = peer;

        var id = Interlocked.Increment(ref _nextSessionId);
        var session = new ProxySession(client, _forwardEndpoint, _config, _stats, cancellationToken);

        _sessions[id] = session;

        try
        {
            await session.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encountered an exception");
        }
        finally
        {
            _sessions.TryRemove(id, out _);

            session.Dispose();

            _stats.ExitSession();
        }
    }

    /// <summary>
    /// Determines whether an accept failure affects only the incoming connection, leaving the listener usable.
    /// </summary>
    /// <param name="error">The socket error reported by the accept call.</param>
    /// <returns><c>true</c> when accepting can continue; otherwise, <c>false</c>.</returns>
    private static bool IsRetryableAcceptError(SocketError error) =>
        error is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset
            or SocketError.TimedOut or SocketError.TryAgain || IsResourceExhaustion(error);

    /// <summary>
    /// Determines whether an accept failure was caused by exhausted socket resources, in which case retrying
    /// immediately would spin the CPU while logging the same error.
    /// </summary>
    /// <param name="error">The socket error reported by the accept call.</param>
    /// <returns><c>true</c> when the failure is caused by resource exhaustion; otherwise, <c>false</c>.</returns>
    private static bool IsResourceExhaustion(SocketError error) =>
        error is SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets;
}
