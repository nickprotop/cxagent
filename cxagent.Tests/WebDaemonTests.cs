using CxAgent.Core.Web;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary><c>cxagent web --daemon</c>, <c>status</c> and <c>stop</c>, and the startup screen.</summary>
public sealed class WebDaemonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("webd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("--daemon")]
    [InlineData("-d")]
    public void Daemon_is_a_flag(string flag)
    {
        var o = WebCli.Parse(["--port", "6001", flag], out var error);
        Assert.Null(error);
        Assert.True(o!.Daemon);
        Assert.False(WebCli.Parse(["--port", "6001"], out _)!.Daemon);
    }

    [Fact]
    public void A_record_of_a_live_server_is_found_and_a_stale_one_is_removed()
    {
        // THIS PROCESS IS A TEST HOST, NOT CXAGENT: its record reads as stale, which is what one left by a crashed or
        // restarted server must do.
        WebDaemon.Record(_dir, new WebRunning(Environment.ProcessId, "http://localhost:1/", DateTimeOffset.Now, Daemon: true));
        Assert.True(File.Exists(WebDaemon.StatePath(_dir)));
        Assert.Null(WebDaemon.Running(_dir));
        Assert.False(File.Exists(WebDaemon.StatePath(_dir)));
    }

    [Fact]
    public void Forgetting_leaves_another_server_s_record_alone()
    {
        // A SECOND SERVER REFUSED AT START must not erase the record of the first, which is still serving.
        WebDaemon.Record(_dir, new WebRunning(424242, "http://localhost:1/", DateTimeOffset.Now, Daemon: true));
        WebDaemon.Forget(_dir, Environment.ProcessId);
        Assert.True(File.Exists(WebDaemon.StatePath(_dir)));
        WebDaemon.Forget(_dir, 424242);
        Assert.False(File.Exists(WebDaemon.StatePath(_dir)));
    }

    [Fact]
    public void Recording_a_server_clears_an_old_stop_request()
    {
        File.WriteAllText(WebDaemon.StopPath(_dir), "");
        WebDaemon.Record(_dir, new WebRunning(1, "http://localhost:1/", DateTimeOffset.Now, Daemon: false));
        Assert.False(File.Exists(WebDaemon.StopPath(_dir)));
    }

    [Fact]
    public async Task A_stop_request_stops_the_server_it_is_watching()
    {
        using var stop = new CancellationTokenSource();
        var watching = WebDaemon.WatchForStopAsync(_dir, stop);
        File.WriteAllText(WebDaemon.StopPath(_dir), "");
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stop.IsCancellationRequested);
    }

    [Fact]
    public void The_startup_screen_says_where_who_what_and_how_to_stop()
    {
        var settings = WebSettings.Default with
        {
            Bind = "0.0.0.0", Users = new Dictionary<string, string> { ["me"] = "pbkdf2:x" },
            Allow = WebAccess.Default with { Shell = true },
        };
        var lines = WebBanner.Lines(new WebBannerFacts("1.2.3", new Uri("http://localhost:5180/"),
            [new Uri("http://192.168.1.20:5180/")], settings, ["/srv/code"], "local", ["Serving plain HTTP beyond this machine."]),
            "Ctrl+C to stop").Select(l => l.Text).ToList();
        var text = string.Join("\n", lines);
        Assert.Contains("cxagent web 1.2.3", text, StringComparison.Ordinal);
        Assert.Contains("Local    http://localhost:5180/", text, StringComparison.Ordinal);
        Assert.Contains("Network  http://192.168.1.20:5180/", text, StringComparison.Ordinal);
        Assert.Contains("login     required · users: me", text, StringComparison.Ordinal);
        Assert.Contains("shell on", text, StringComparison.Ordinal);
        Assert.Contains("! Serving plain HTTP", text, StringComparison.Ordinal);
        Assert.EndsWith("Ctrl+C to stop", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_server_listening_everywhere_lists_network_addresses() =>
        Assert.Empty(WebBanner.NetworkAddresses("127.0.0.1", "http", 5180));
}
