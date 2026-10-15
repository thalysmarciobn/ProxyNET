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

    private readonly ForwardingConfig _config;
    private readonly IPEndPoint _listenEndpoint;
    private readonly EndPoint _forwardEndpoint;
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();

    private TcpListener? _listener;

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
    }

    /// <summary>
    /// The endpoint the listener is actually bound to, available once <see cref="Start"/> succeeded. It differs
    /// from the configured one when port <c>0</c> was requested.
    /// </summary>
    public IPEndPoint? BoundEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    /// <summary>
    /// The number of sessions currently being forwarded.
    /// </summary>
    public int ActiveSessions => _sessions.Count;

    /// <summary>
    /// Releases the listening socket.
    /// </summary>
    public void Dispose() => _listener?.Dispose();

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

        Log.Information("Proxy listening on {ListenEndpoint}, forwarding to {ForwardEndpoint}", listener.LocalEndpoint,
            _forwardEndpoint);
    }

    /// <summary>
    /// Accepts connections until <paramref name="cancellationToken"/> is cancelled or the listener can no longer be
    /// used. Failures that leave the listener unusable end the loop instead of turning it into a busy error loop.
    /// </summary>
    /// <param name="cancellationToken">A token that stops the accept loop and the running sessions when cancelled.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Start"/> has not been called.</exception>
    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        var listener = _listener ??
                       throw new InvalidOperationException("Start must be called before accepting connections.");
        var endpoint = listener.LocalEndpoint;

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
                Log.Warning("Error accepting a connection on {Endpoint}. SocketErrorCode: {SocketErrorCode}", endpoint,
                    ex.SocketErrorCode);

                if (IsResourceExhaustion(ex.SocketErrorCode))
                    await Task.Delay(AcceptBackoffMilliseconds, CancellationToken.None);

                continue;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Stopped accepting connections on {Endpoint}", endpoint);

                break;
            }

            TrackSession(HandleClientAsync(peer, cancellationToken));
        }

        Log.Information("Proxy on {Endpoint} stopped accepting connections", endpoint);
    }

    /// <summary>
    /// Stops listening and waits for the running sessions to finish.
    /// </summary>
    /// <param name="drainTimeout">How long to wait for the running sessions before giving up.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task StopAsync(TimeSpan drainTimeout)
    {
        _listener?.Stop();

        var pending = _sessions.Keys.ToArray();

        if (pending.Length == 0)
            return;

        Log.Information("Waiting for {Count} active session(s) on {Endpoint} to finish", pending.Length,
            _listenEndpoint);

        try
        {
            await Task.WhenAll(pending).WaitAsync(drainTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warning("{Count} session(s) on {Endpoint} did not finish within {Timeout}", _sessions.Count,
                _listenEndpoint, drainTimeout);
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

        try
        {
            var session = new ProxySession(client, _forwardEndpoint, _config);

            await session.RunAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encountered an exception");
        }
    }

    /// <summary>
    /// Keeps track of a running session so it can be awaited during shutdown.
    /// </summary>
    /// <param name="session">The task representing the running session.</param>
    private void TrackSession(Task session)
    {
        _sessions[session] = 0;

        _ = session.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _sessions, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
