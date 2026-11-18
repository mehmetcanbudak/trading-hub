using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.App.Services.LocalApi;

namespace TradingHub.Tests;

/// <summary>
/// The settings-driven lifecycle of the opt-in local market-data API: toggling, port changes, ports held by other
/// programs, origin validation and starting from saved settings. Every server binds 127.0.0.1 on a free port.
/// </summary>
public sealed class LocalApiSettingsTests
{
    [Fact]
    public async Task Toggling_on_off_and_on_again_listens_closes_and_listens()
    {
        int port = FreePort();
        var host = new LocalApiHost(() => new NullSource());
        var settings = new AppSettings { LocalApiPort = port };
        try
        {
            Assert.Equal("Off", host.Status);
            settings.LocalApiEnabled = true;
            host.Apply(settings);
            Assert.True(host.Api is { IsRunning: true });
            Assert.Equal($"Listening on http://127.0.0.1:{port}/api/v1", host.Status);
            Assert.True(await AcceptsAsync(port));

            settings.LocalApiEnabled = false;
            host.Apply(settings);
            Assert.Null(host.Api);
            Assert.Equal("Off", host.Status);
            Assert.True(await RefusedAsync(port));

            settings.LocalApiEnabled = true;
            host.Apply(settings);
            Assert.True(host.Api is { IsRunning: true });
            Assert.Equal($"Listening on http://127.0.0.1:{port}/api/v1", host.Status);
            Assert.True(await AcceptsAsync(port));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task Applying_unchanged_settings_keeps_the_same_server()
    {
        var host = new LocalApiHost(() => new NullSource());
        var settings = new AppSettings { LocalApiEnabled = true, LocalApiPort = FreePort() };
        try
        {
            host.Apply(settings);
            var first = host.Api;
            host.Apply(settings);
            Assert.Same(first, host.Api);
            settings.LocalApiAllowedOrigins = ["http://localhost:3000"];
            host.Apply(settings);
            Assert.NotSame(first, host.Api);
            Assert.True(host.Api is { IsRunning: true });
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task A_port_change_restarts_on_the_new_port_and_the_old_port_refuses()
    {
        int oldPort = FreePort();
        var host = new LocalApiHost(() => new NullSource());
        var settings = new AppSettings { LocalApiEnabled = true, LocalApiPort = oldPort };
        try
        {
            host.Apply(settings);
            Assert.True(await AcceptsAsync(oldPort));
            int newPort = FreePort();
            settings.LocalApiPort = newPort;
            host.Apply(settings);
            Assert.Equal(newPort, host.Api?.Endpoint?.Port);
            Assert.Equal($"Listening on http://127.0.0.1:{newPort}/api/v1", host.Status);
            Assert.True(await AcceptsAsync(newPort));
            Assert.True(await RefusedAsync(oldPort));
        }
        finally { await host.ShutdownAsync(); }
    }

    public static TheoryData<string> Occupants => new() { "loopback", "any", "dual-stack", "ipv6-loopback", "ipv6-any" };

    [Theory]
    [MemberData(nameof(Occupants))]
    public async Task A_port_held_by_another_listener_is_reported_and_reapplying_after_it_is_freed_starts(string occupant)
    {
        // Without IPv6 nothing can listen on ::1 or [::], so there is nothing to detect.
        if (occupant.StartsWith("ipv6", StringComparison.Ordinal) && !Socket.OSSupportsIPv6) return;
        var holder = occupant switch
        {
            "loopback" => new TcpListener(IPAddress.Loopback, 0),
            "any" => new TcpListener(IPAddress.Any, 0),
            // A program on ::1 would receive the connections of clients that resolve "localhost" to ::1.
            "ipv6-loopback" => new TcpListener(IPAddress.IPv6Loopback, 0),
            "ipv6-any" => new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = false } },
            _ => new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = true } },
        };
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        var host = new LocalApiHost(() => new NullSource());
        var settings = new AppSettings { LocalApiEnabled = true, LocalApiPort = port };
        try
        {
            host.Apply(settings);
            Assert.Null(host.Api);
            Assert.Equal($"Port {port} is already in use.", host.Status);

            holder.Stop();
            host.Apply(settings);
            Assert.True(host.Api is { IsRunning: true });
            Assert.Equal($"Listening on http://127.0.0.1:{port}/api/v1", host.Status);
            Assert.True(await AcceptsAsync(port));
        }
        finally
        {
            holder.Stop();
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Shutdown_closes_the_listener_and_settings_cannot_restart_it()
    {
        int port = FreePort();
        var host = new LocalApiHost(() => new NullSource());
        var settings = new AppSettings { LocalApiEnabled = true, LocalApiPort = port };
        host.Apply(settings);
        await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(await RefusedAsync(port));
        host.Apply(settings);
        Assert.Null(host.Api);
        Assert.Equal("Off", host.Status);
    }

    [Fact]
    public async Task Saved_settings_with_the_api_on_start_it_when_applied_at_startup()
    {
        int port = FreePort();
        var json = new JsonSerializerOptions { Converters = { new TradingHub.Charting.Dom.ColorJsonConverter() } };
        string saved = JsonSerializer.Serialize(new AppSettings { LocalApiEnabled = true, LocalApiPort = port, LocalApiAllowedOrigins = ["http://localhost:5173"] }, json);
        var loaded = JsonSerializer.Deserialize<AppSettings>(saved, json)!;
        Assert.True(loaded.LocalApiEnabled);
        Assert.Equal(port, loaded.LocalApiPort);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{}", json)!.LocalApiEnabled);

        var host = new LocalApiHost(() => new NullSource());
        try
        {
            host.Apply(loaded);
            Assert.True(host.Api is { IsRunning: true });
            Assert.True(await AcceptsAsync(port));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public void Origin_validation_lists_every_invalid_entry_and_keeps_distinct_normalized_origins()
    {
        var invalid = new List<string>();
        var valid = LocalMarketDataApiOptions.NormalizeOrigins(
            ["http://localhost:3000/", "ftp://example.com", " https://Tool.Example ", "http://localhost:3000", "localhost:8080", "http://user@host", "http://host/path"], invalid);
        Assert.Equal(["http://localhost:3000", "https://tool.example"], valid);
        Assert.Equal(["ftp://example.com", "localhost:8080", "http://user@host", "http://host/path"], invalid);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> AcceptsAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static async Task<bool> RefusedAsync(int port) => !await AcceptsAsync(port);

    private sealed class NullSource : ILocalMarketDataSource
    {
        public Task<ApiResult<ApiInfo>> GetInfoAsync(CancellationToken ct) => Task.FromResult(ApiResult<ApiInfo>.Ok(new ApiInfo("Trading Hub", "test")));
        public Task<ApiResult<IReadOnlyList<ApiConnection>>> GetConnectionsAsync(CancellationToken ct) => Unavailable<IReadOnlyList<ApiConnection>>();
        public Task<ApiResult<IReadOnlyList<ApiOpenInstrument>>> GetOpenInstrumentsAsync(CancellationToken ct) => Unavailable<IReadOnlyList<ApiOpenInstrument>>();
        public Task<ApiResult<IReadOnlyList<ApiKline>>> GetKlinesAsync(ApiKlineRequest request, CancellationToken ct) => Unavailable<IReadOnlyList<ApiKline>>();
        public Task<ApiResult<IReadOnlyList<ApiTrade>>> GetTradesAsync(string connection, string symbol, int limit, CancellationToken ct) => Unavailable<IReadOnlyList<ApiTrade>>();
        public Task<ApiResult<ApiBook>> GetDepthAsync(string connection, string symbol, int levels, CancellationToken ct) => Unavailable<ApiBook>();
        public Task<ApiResult<ApiLiquidity>> GetLiquidityAsync(string connection, string symbol, TimeSpan window, int levels, int maxSamples, CancellationToken ct) => Unavailable<ApiLiquidity>();
        public Task<ApiResult<ApiStreamSubscription>> SubscribeStreamAsync(string connection, string symbol, Action<ApiTradeBatch> onTrades, Action<Func<int, ApiBook>> onBook, CancellationToken ct) => Unavailable<ApiStreamSubscription>();
        private static Task<ApiResult<T>> Unavailable<T>() => Task.FromResult(ApiResult<T>.Unavailable("Not available in this test."));
    }
}
