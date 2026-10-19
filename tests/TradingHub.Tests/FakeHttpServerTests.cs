namespace TradingHub.Tests;

public class FakeHttpServerTests
{
    [Fact]
    public async Task Busy_port_retry_keeps_both_servers_usable()
    {
        using var occupied = new FakeHttpServer();
        occupied.OnHttp = _ => (200, "occupied");
        using var retried = new FakeHttpServer(firstPort: occupied.Port);
        retried.OnHttp = _ => (200, "retried");

        Assert.NotEqual(occupied.Port, retried.Port);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("occupied", await client.GetStringAsync(occupied.Http));
        Assert.Equal("retried", await client.GetStringAsync(retried.Http));
    }
}
