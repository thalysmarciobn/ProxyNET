namespace ProxyNET;

/// <summary>
/// The direction data flows in within a session.
/// </summary>
public enum ProxyDirection
{
    /// <summary>From the connected peer towards the forwarding target.</summary>
    PeerToTarget,

    /// <summary>From the forwarding target back to the connected peer.</summary>
    TargetToPeer
}

/// <summary>
/// An immutable reading of the counters of a listener.
/// </summary>
/// <param name="Active">Sessions currently being forwarded.</param>
/// <param name="Accepted">Connections accepted since start.</param>
/// <param name="Rejected">Connections closed immediately because the session limit was reached.</param>
/// <param name="Failed">Sessions that could not reach the target.</param>
/// <param name="IdleTimeouts">Sessions aborted for making no progress.</param>
/// <param name="BytesToTarget">Bytes forwarded from the peers to the target.</param>
/// <param name="BytesToPeer">Bytes forwarded from the target back to the peers.</param>
public readonly record struct ProxyStatsSnapshot(
    long Active,
    long Accepted,
    long Rejected,
    long Failed,
    long IdleTimeouts,
    long BytesToTarget,
    long BytesToPeer);

/// <summary>
/// The counters of a single listener. Byte counters are meant to be fed in batches rather than per transfer, so a
/// saturated proxy does not contend on the same cache line for every chunk it forwards.
/// </summary>
public sealed class ProxyStats
{
    private long _active;
    private long _accepted;
    private long _rejected;
    private long _failed;
    private long _idleTimeouts;
    private long _bytesToTarget;
    private long _bytesToPeer;
    private long _lastTargetWarningTicks;

    /// <summary>
    /// Sessions currently being forwarded.
    /// </summary>
    public long Active => Volatile.Read(ref _active);

    /// <summary>
    /// Takes a reading of every counter.
    /// </summary>
    /// <returns>The current values.</returns>
    public ProxyStatsSnapshot Snapshot() => new(
        Volatile.Read(ref _active),
        Volatile.Read(ref _accepted),
        Volatile.Read(ref _rejected),
        Volatile.Read(ref _failed),
        Volatile.Read(ref _idleTimeouts),
        Volatile.Read(ref _bytesToTarget),
        Volatile.Read(ref _bytesToPeer));

    /// <summary>
    /// Reserves a slot for a new session unless the configured limit is already reached.
    /// </summary>
    /// <param name="maxConnections">The maximum number of concurrent sessions, or <c>0</c> when unlimited.</param>
    /// <returns><c>true</c> when the session may run; otherwise, <c>false</c>.</returns>
    public bool TryEnterSession(int maxConnections)
    {
        var active = Interlocked.Increment(ref _active);

        if (maxConnections <= 0 || active <= maxConnections)
        {
            Interlocked.Increment(ref _accepted);

            return true;
        }

        Interlocked.Decrement(ref _active);
        Interlocked.Increment(ref _rejected);

        return false;
    }

    /// <summary>
    /// Releases the slot taken by <see cref="TryEnterSession"/>.
    /// </summary>
    public void ExitSession() => Interlocked.Decrement(ref _active);

    /// <summary>
    /// Counts a session that could not reach the target.
    /// </summary>
    public void OnTargetUnreachable() => Interlocked.Increment(ref _failed);

    /// <summary>
    /// Counts a session aborted for making no progress.
    /// </summary>
    public void OnIdleTimeout() => Interlocked.Increment(ref _idleTimeouts);

    /// <summary>
    /// Decides whether an unreachable target should be reported, so a target that is down does not turn every one
    /// of its refused sessions into a log entry. The counters keep the full picture either way.
    /// </summary>
    /// <param name="intervalMilliseconds">The minimum time between two reports.</param>
    /// <returns><c>true</c> for the caller that may report; otherwise, <c>false</c>.</returns>
    public bool ShouldReportUnreachableTarget(int intervalMilliseconds)
    {
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastTargetWarningTicks);

        return now - last >= intervalMilliseconds &&
               Interlocked.CompareExchange(ref _lastTargetWarningTicks, now, last) == last;
    }

    /// <summary>
    /// Adds forwarded bytes to the counter of a direction.
    /// </summary>
    /// <param name="direction">The direction the bytes were forwarded in.</param>
    /// <param name="bytes">The number of bytes to add.</param>
    public void AddBytes(ProxyDirection direction, long bytes)
    {
        if (direction == ProxyDirection.PeerToTarget)
            Interlocked.Add(ref _bytesToTarget, bytes);
        else
            Interlocked.Add(ref _bytesToPeer, bytes);
    }
}
