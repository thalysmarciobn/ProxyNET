using ProxyNET.Configuration;
using Xunit;

namespace ProxyNET.Tests;

public class ForwardingConfigTests
{
    private static ForwardingConfig ValidConfig() => new()
    {
        Address = "127.0.0.1",
        Port = 25500,
        Forward = new ForwardConfig { Address = "127.0.0.1", Port = 8090 }
    };

    [Fact]
    public void Validate_ValidConfig_ReportsNoErrors()
    {
        Assert.Empty(ValidConfig().Validate(0));
    }

    [Fact]
    public void Validate_PortZero_IsAcceptedForTheListener()
    {
        var config = ValidConfig();

        config.Port = 0;

        Assert.Empty(config.Validate(0));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-an-address")]
    [InlineData("300.1.1.1")]
    public void Validate_InvalidListenAddress_IsReported(string address)
    {
        var config = ValidConfig();

        config.Address = address;

        Assert.Contains(config.Validate(0), error => error.Contains("Forwarding[0].Address"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("any")]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    public void Validate_SupportedListenAddress_IsAccepted(string address)
    {
        var config = ValidConfig();

        config.Address = address;

        Assert.Empty(config.Validate(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_ListenPortOutOfRange_IsReported(int port)
    {
        var config = ValidConfig();

        config.Port = port;

        Assert.Contains(config.Validate(0), error => error.Contains("Forwarding[0].Port"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Validate_ForwardPortOutOfRange_IsReported(int port)
    {
        var config = ValidConfig();

        config.Forward.Port = port;

        Assert.Contains(config.Validate(0), error => error.Contains("Forwarding[0].Forward.Port"));
    }

    [Fact]
    public void Validate_MissingForwardSection_IsReportedInsteadOfThrowing()
    {
        var config = ValidConfig();

        config.Forward = null!;

        Assert.Contains(config.Validate(0), error => error.Contains("Forwarding[0].Forward"));
    }

    [Fact]
    public void Validate_NegativeTimeouts_AreReported()
    {
        var config = ValidConfig();

        config.ConnectTimeout = -1;
        config.ReadTimeout = -1;
        config.WriteTimeout = -1;

        var errors = config.Validate(0).ToList();

        Assert.Contains(errors, error => error.Contains("ConnectTimeout"));
        Assert.Contains(errors, error => error.Contains("ReadTimeout"));
        Assert.Contains(errors, error => error.Contains("WriteTimeout"));
    }

    [Fact]
    public void Validate_UsesTheGivenIndexInTheMessages()
    {
        var config = ValidConfig();

        config.Port = -1;

        Assert.Contains(config.Validate(3), error => error.StartsWith("Forwarding[3]"));
    }

    [Theory]
    [InlineData(0, Timeout.Infinite)]
    [InlineData(-5, Timeout.Infinite)]
    [InlineData(1500, 1500)]
    public void ToTimeoutMilliseconds_MapsDisabledTimeoutsToInfinite(int configured, int expected)
    {
        Assert.Equal(expected, ForwardingConfig.ToTimeoutMilliseconds(configured));
    }

    [Fact]
    public void ConnectTimeout_HasADefault()
    {
        Assert.Equal(ForwardingConfig.DefaultConnectTimeout, ValidConfig().ConnectTimeout);
    }
}
