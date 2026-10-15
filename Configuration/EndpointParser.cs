using System.Net;

namespace ProxyNET.Configuration;

/// <summary>
/// Translates the textual addresses coming from the configuration into endpoints.
/// </summary>
public static class EndpointParser
{
    /// <summary>
    /// The lowest valid TCP port for an outgoing connection.
    /// </summary>
    public const int MinPort = 1;

    /// <summary>
    /// The highest valid TCP port.
    /// </summary>
    public const int MaxPort = 65535;

    /// <summary>
    /// Parses an address the proxy can bind to. Besides IP literals, the aliases
    /// <c>*</c>, <c>+</c> and <c>any</c> (all interfaces) and <c>localhost</c> are accepted.
    /// </summary>
    /// <param name="address">The address as written in the configuration.</param>
    /// <param name="parsed">The parsed address when the method returns true.</param>
    /// <returns><c>true</c> when the address could be parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParseListenAddress(string? address, out IPAddress parsed)
    {
        parsed = IPAddress.None;

        if (string.IsNullOrWhiteSpace(address))
            return false;

        switch (address.Trim())
        {
            case "*":
            case "+":
            case "any":
                parsed = IPAddress.Any;
                return true;
            case "localhost":
                parsed = IPAddress.Loopback;
                return true;
            default:
                if (!IPAddress.TryParse(address.Trim(), out var ip))
                    return false;

                parsed = ip;
                return true;
        }
    }

    /// <summary>
    /// Builds the endpoint a session connects to. IP literals are used as-is, while host names are kept
    /// as a <see cref="DnsEndPoint"/> so they are resolved per connection instead of once at startup.
    /// </summary>
    /// <param name="address">The IP address or host name of the target.</param>
    /// <param name="port">The TCP port of the target.</param>
    /// <returns>The endpoint describing the target.</returns>
    public static EndPoint ParseForwardEndpoint(string address, int port) =>
        IPAddress.TryParse(address.Trim(), out var ip)
            ? new IPEndPoint(ip, port)
            : new DnsEndPoint(address.Trim(), port);

    /// <summary>
    /// Determines whether a port can be used for an outgoing connection.
    /// </summary>
    /// <param name="port">The port to check.</param>
    /// <returns><c>true</c> when the port is within the valid range; otherwise, <c>false</c>.</returns>
    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>
    /// Determines whether a port can be used to bind a listener. Port <c>0</c> is valid and lets the
    /// operating system pick a free port.
    /// </summary>
    /// <param name="port">The port to check.</param>
    /// <returns><c>true</c> when the port is within the valid range; otherwise, <c>false</c>.</returns>
    public static bool IsValidListenPort(int port) => port is >= 0 and <= MaxPort;
}
