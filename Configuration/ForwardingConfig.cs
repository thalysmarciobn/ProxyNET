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
    /// The default size, in bytes, of the pooled buffer used for each direction of a session.
    /// </summary>
    public const int DefaultBufferSize = 16 * 1024;

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
    /// The amount of time, in milliseconds, a direction of the connection may stay idle before the session is
    /// closed. Use <c>0</c> to keep idle connections open indefinitely.
    /// </summary>
    public int ReadTimeout { get; set; }

    /// <summary>
    /// The amount of time, in milliseconds, a single write to either side may take before the session is closed.
    /// Use <c>0</c> to wait indefinitely.
    /// </summary>
    public int WriteTimeout { get; set; }

    /// <summary>
    /// The size, in bytes, of the pooled buffer used for each direction of a session. Larger buffers mean fewer
    /// system calls per transferred byte, at the cost of more memory per connection.
    /// </summary>
    public int BufferSize { get; set; } = DefaultBufferSize;

    /// <summary>
    /// The target the incoming traffic is forwarded to.
    /// </summary>
    public ForwardConfig Forward { get; set; } = new();

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

        if (ReadTimeout < 0)
            yield return $"{prefix}.ReadTimeout: {ReadTimeout} must be 0 (disabled) or greater.";

        if (WriteTimeout < 0)
            yield return $"{prefix}.WriteTimeout: {WriteTimeout} must be 0 (disabled) or greater.";

        if (BufferSize is < MinBufferSize or > MaxBufferSize)
            yield return $"{prefix}.BufferSize: {BufferSize} is outside the valid range ({MinBufferSize}-{MaxBufferSize}).";

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
