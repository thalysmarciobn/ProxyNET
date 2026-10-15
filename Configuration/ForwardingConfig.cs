namespace ProxyNET.Configuration;

/// <summary>
/// Represents a single proxy listener together with the target its traffic is forwarded to.
/// </summary>
public class ForwardingConfig
{
    /// <summary>
    /// The default amount of time, in milliseconds, allowed for the connection to the target to be established.
    /// </summary>
    public const int DefaultConnectTimeout = 10_000;

    /// <summary>
    /// The default size, in bytes, of the pooled buffer used while a transfer is in flight. It is the largest
    /// power of two that stays off the large object heap, so the pool can recycle it indefinitely.
    /// </summary>
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>
    /// The smallest accepted value for <see cref="BufferSize"/>.
    /// </summary>
    public const int MinBufferSize = 1024;

    /// <summary>
    /// The largest accepted value for <see cref="BufferSize"/>.
    /// </summary>
    public const int MaxBufferSize = 1024 * 1024;

    /// <summary>
    /// The local address the proxy binds to. Accepts an IP literal, <c>*</c>/<c>+</c>/<c>any</c> or <c>localhost</c>.
    /// Mandatory, validated by <see cref="Validate"/>.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// The local TCP port the proxy binds to. Use <c>0</c> to let the operating system pick a free port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// The amount of time, in milliseconds, allowed for the connection to the target to be established.
    /// Use <c>0</c> to wait for as long as the operating system allows.
    /// </summary>
    public int ConnectTimeout { get; set; } = DefaultConnectTimeout;

    /// <summary>
    /// The amount of time, in milliseconds, a session may make no progress at all before it is aborted. Covers
    /// idle reads as well as writes to a stalled destination, and is enforced by a single sweep per listener
    /// instead of a timer per transfer. Use <c>0</c> to keep sessions open for as long as both sockets live.
    /// </summary>
    public int IdleTimeout { get; set; }

    /// <summary>
    /// Legacy name of <see cref="IdleTimeout"/>, honoured when <see cref="IdleTimeout"/> is not set.
    /// </summary>
    public int ReadTimeout { get; set; }

    /// <summary>
    /// The amount of time, in milliseconds, a single write may take before the session is aborted. Enforcing it
    /// costs a timer per forwarded chunk, so prefer <see cref="IdleTimeout"/> on high throughput deployments and
    /// use <c>0</c> to disable it.
    /// </summary>
    public int WriteTimeout { get; set; }

    /// <summary>
    /// The size, in bytes, of the pooled buffer rented while a transfer is in flight. Larger buffers mean fewer
    /// system calls per transferred byte, at the cost of more memory per concurrently active transfer.
    /// </summary>
    public int BufferSize { get; set; } = DefaultBufferSize;

    /// <summary>
    /// The maximum number of concurrent sessions. Connections beyond the limit are closed right after being
    /// accepted. Use <c>0</c> to accept as many sessions as the operating system allows.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Whether to wait for data with a zero byte receive before renting a buffer, so idle sessions hold no buffer
    /// at all and memory scales with the active transfers instead of the connection count. Disabling it saves one
    /// receive per chunk on deployments that are busy rather than numerous.
    /// </summary>
    public bool WaitForData { get; set; } = true;

    /// <summary>
    /// Whether to disable Nagle on both sockets of a session.
    /// </summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>
    /// The size, in bytes, of the receive buffer of both sockets. Use <c>0</c> to keep the value of the operating
    /// system, which on Linux also keeps its receive buffer auto tuning enabled.
    /// </summary>
    public int ReceiveBufferSize { get; set; }

    /// <summary>
    /// The size, in bytes, of the send buffer of both sockets. Use <c>0</c> to keep the value of the operating
    /// system, which on Linux also keeps its send buffer auto tuning enabled.
    /// </summary>
    public int SendBufferSize { get; set; }

    /// <summary>
    /// The target the incoming traffic is forwarded to.
    /// </summary>
    public ForwardConfig Forward { get; set; } = new();

    /// <summary>
    /// The no progress timeout actually in use, taken from <see cref="ReadTimeout"/> when
    /// <see cref="IdleTimeout"/> is not set.
    /// </summary>
    public int EffectiveIdleTimeout => IdleTimeout > 0 ? IdleTimeout : ReadTimeout;

    /// <summary>
    /// Whether the entry relies on the legacy <see cref="ReadTimeout"/> name.
    /// </summary>
    public bool UsesLegacyReadTimeout => IdleTimeout <= 0 && ReadTimeout > 0;

    /// <summary>
    /// Converts a configured timeout into a value usable by <see cref="CancellationTokenSource.CancelAfter(int)"/>,
    /// mapping <c>0</c> to <see cref="Timeout.Infinite"/>.
    /// </summary>
    /// <param name="timeout">The configured timeout in milliseconds.</param>
    /// <returns>The timeout in milliseconds, or <see cref="Timeout.Infinite"/> when disabled.</returns>
    public static int ToTimeoutMilliseconds(int timeout) => timeout <= 0 ? Timeout.Infinite : timeout;

    /// <summary>
    /// Validates the configuration. Configuration binding cannot enforce mandatory members, so the values have to
    /// be checked explicitly before they are used.
    /// </summary>
    /// <param name="index">The index of this entry inside the <c>Forwarding</c> array, used in the messages.</param>
    /// <returns>A message for every problem found; empty when the entry is valid.</returns>
    public IEnumerable<string> Validate(int index)
    {
        var prefix = $"Forwarding[{index}]";

        if (!EndpointParser.TryParseListenAddress(Address, out _))
            yield return $"{prefix}.Address: {Address} is not a valid local address. Use an IP address, * or localhost.";

        if (!EndpointParser.IsValidListenPort(Port))
            yield return $"{prefix}.Port: {Port} is outside the valid range (0-{EndpointParser.MaxPort}).";

        if (ConnectTimeout < 0)
            yield return $"{prefix}.ConnectTimeout: {ConnectTimeout} must be 0 (disabled) or greater.";

        if (IdleTimeout < 0)
            yield return $"{prefix}.IdleTimeout: {IdleTimeout} must be 0 (disabled) or greater.";

        if (ReadTimeout < 0)
            yield return $"{prefix}.ReadTimeout: {ReadTimeout} must be 0 (disabled) or greater.";

        if (WriteTimeout < 0)
            yield return $"{prefix}.WriteTimeout: {WriteTimeout} must be 0 (disabled) or greater.";

        if (BufferSize is < MinBufferSize or > MaxBufferSize)
            yield return $"{prefix}.BufferSize: {BufferSize} is outside the valid range ({MinBufferSize}-{MaxBufferSize}).";

        if (MaxConnections < 0)
            yield return $"{prefix}.MaxConnections: {MaxConnections} must be 0 (unlimited) or greater.";

        if (ReceiveBufferSize < 0)
            yield return $"{prefix}.ReceiveBufferSize: {ReceiveBufferSize} must be 0 (operating system default) or greater.";

        if (SendBufferSize < 0)
            yield return $"{prefix}.SendBufferSize: {SendBufferSize} must be 0 (operating system default) or greater.";

        if (Forward is null)
        {
            yield return $"{prefix}.Forward: section is missing.";

            yield break;
        }

        if (string.IsNullOrWhiteSpace(Forward.Address))
            yield return $"{prefix}.Forward.Address: a target IP address or host name is required.";

        if (!EndpointParser.IsValidPort(Forward.Port))
            yield return
                $"{prefix}.Forward.Port: {Forward.Port} is outside the valid range ({EndpointParser.MinPort}-{EndpointParser.MaxPort}).";
    }
}
