namespace ProxyNET.Configuration;

/// <summary>
/// Describes the target endpoint that a <see cref="ForwardingConfig"/> forwards traffic to.
/// </summary>
public class ForwardConfig
{
    /// <summary>
    /// The IP address or host name of the target. Host names are resolved when a connection is established.
    /// Mandatory, validated by <see cref="ForwardingConfig.Validate"/>.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// The TCP port of the target. Mandatory, validated by <see cref="ForwardingConfig.Validate"/>.
    /// </summary>
    public int Port { get; set; }
}
