using Xunit;

namespace ProxyNET.Tests;

public class ProxyStatsTests
{
    [Fact]
    public void TryEnterSession_WithoutLimit_AlwaysEnters()
    {
        var stats = new ProxyStats();

        for (var session = 0; session < 10; session++)
            Assert.True(stats.TryEnterSession(0));

        Assert.Equal(10, stats.Active);
        Assert.Equal(10, stats.Snapshot().Accepted);
        Assert.Equal(0, stats.Snapshot().Rejected);
    }

    [Fact]
    public void TryEnterSession_BeyondTheLimit_IsRejectedAndCounted()
    {
        var stats = new ProxyStats();

        Assert.True(stats.TryEnterSession(2));
        Assert.True(stats.TryEnterSession(2));
        Assert.False(stats.TryEnterSession(2));

        var snapshot = stats.Snapshot();

        Assert.Equal(2, snapshot.Active);
        Assert.Equal(2, snapshot.Accepted);
        Assert.Equal(1, snapshot.Rejected);
    }

    [Fact]
    public void ExitSession_FreesTheSlot()
    {
        var stats = new ProxyStats();

        Assert.True(stats.TryEnterSession(1));
        Assert.False(stats.TryEnterSession(1));

        stats.ExitSession();

        Assert.True(stats.TryEnterSession(1));
    }

    [Fact]
    public void AddBytes_CountsEachDirectionSeparately()
    {
        var stats = new ProxyStats();

        stats.AddBytes(ProxyDirection.PeerToTarget, 1000);
        stats.AddBytes(ProxyDirection.PeerToTarget, 500);
        stats.AddBytes(ProxyDirection.TargetToPeer, 7);

        var snapshot = stats.Snapshot();

        Assert.Equal(1500, snapshot.BytesToTarget);
        Assert.Equal(7, snapshot.BytesToPeer);
    }

    [Fact]
    public void ShouldReportUnreachableTarget_ThrottlesTheReports()
    {
        var stats = new ProxyStats();

        Assert.True(stats.ShouldReportUnreachableTarget(60_000));

        for (var attempt = 0; attempt < 100; attempt++)
            Assert.False(stats.ShouldReportUnreachableTarget(60_000));

        Assert.True(stats.ShouldReportUnreachableTarget(0));
    }
}
