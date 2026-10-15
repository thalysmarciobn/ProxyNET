using System.Net;
using ProxyNET.Configuration;
using Xunit;

namespace ProxyNET.Tests;

public class EndpointParserTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("any")]
    [InlineData(" any ")]
    public void TryParseListenAddress_Wildcards_MapToAnyAddress(string address)
    {
        Assert.True(EndpointParser.TryParseListenAddress(address, out var parsed));
        Assert.Equal(IPAddress.Any, parsed);
    }

    [Fact]
    public void TryParseListenAddress_Localhost_MapsToLoopback()
    {
        Assert.True(EndpointParser.TryParseListenAddress("localhost", out var parsed));
        Assert.Equal(IPAddress.Loopback, parsed);
    }

    [Fact]
    public void TryParseListenAddress_IpLiteral_IsParsed()
    {
        Assert.True(EndpointParser.TryParseListenAddress("192.168.1.10", out var parsed));
        Assert.Equal(IPAddress.Parse("192.168.1.10"), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("1.2.3.4.5")]
    public void TryParseListenAddress_InvalidAddress_ReturnsFalse(string? address)
    {
        Assert.False(EndpointParser.TryParseListenAddress(address, out _));
    }

    [Fact]
    public void ParseForwardEndpoint_IpLiteral_YieldsIpEndpoint()
    {
        var endpoint = Assert.IsType<IPEndPoint>(EndpointParser.ParseForwardEndpoint("10.0.0.5", 8090));

        Assert.Equal(IPAddress.Parse("10.0.0.5"), endpoint.Address);
        Assert.Equal(8090, endpoint.Port);
    }

    [Fact]
    public void ParseForwardEndpoint_HostName_YieldsDnsEndpointSoItIsResolvedPerConnection()
    {
        var endpoint = Assert.IsType<DnsEndPoint>(EndpointParser.ParseForwardEndpoint(" example.com ", 443));

        Assert.Equal("example.com", endpoint.Host);
        Assert.Equal(443, endpoint.Port);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(-1, false)]
    public void IsValidPort_RejectsPortsOutsideTheConnectableRange(int port, bool expected)
    {
        Assert.Equal(expected, EndpointParser.IsValidPort(port));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(-1, false)]
    public void IsValidListenPort_AllowsPortZero(int port, bool expected)
    {
        Assert.Equal(expected, EndpointParser.IsValidListenPort(port));
    }
}
