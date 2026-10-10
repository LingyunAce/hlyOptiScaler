using Deployer.Core.Domain;
using Deployer.Core.HealthCheck;
using System.Diagnostics;
using Xunit;

namespace Deployer.Core.Tests;

public sealed class HealthCheckTests
{
    [Fact]
    public void Physical_log_reader_returns_appended_text_and_honors_character_limit()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "old"); var reader = new FileHealthLogReader(); var offset = reader.Length(path);
            File.AppendAllText(path, "startup"); Assert.Equal("startup", reader.ReadFrom(path, offset, 100));
            Assert.Equal(4, reader.ReadFrom(path, 0, 4).Length);
        }
        finally { File.Delete(path); }
    }

    private sealed class Logs : IHealthLogReader
    {
        public string Text { get; set; } = "old log\n";
        public long Length(string path) => Text.Length;
        public string ReadFrom(string path, long offset, int max) => Text.Substring((int)Math.Min(Text.Length, offset), (int)Math.Min(max, Text.Length - Math.Min(Text.Length, offset)));
    }
    private sealed class Clock(Action tick) : IHealthClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) { ct.ThrowIfCancellationRequested(); UtcNow += delay; tick(); return Task.CompletedTask; }
    }
    private sealed class Launcher(Action onLaunch) : IGameLauncher
    {
        public int Calls { get; private set; }
        public LaunchRequest? LastRequest { get; private set; }
        public IRunningGame Launch(LaunchRequest request) { Calls++; LastRequest = request; onLaunch(); return new Running(); }
        private sealed class Running : IRunningGame { public bool HasExited => false; public void Dispose() { } }
    }
    private static HealthCheckRequest Request() => new()
    {
        Game = new("Game", GamePlatform.Manual, null, null, @"D:\Games\One", @"D:\Games\One", "Game.exe", "0123abcd"),
        ExpectedVersion = "0.9.1", ExpectedBackend = "ffx", Timeout = TimeSpan.FromMilliseconds(100), PollMilliseconds = 50,
    };

    [Fact]
    public async Task Steam_health_launch_uses_platform_app_id_without_starting_executable()
    {
        var launcher = new Launcher(() => { });
        var request = Request() with { Game = Request().Game with { Platform = GamePlatform.Steam, AppId = "12345" } };
        await new HealthCheckRunner(launcher, new Logs(), new Clock(() => { })).RunAsync(request);
        Assert.Equal("steam://run/12345", launcher.LastRequest?.Executable);
        Assert.True(launcher.LastRequest?.IsShellUri);
    }

    [Theory]
    [InlineData("FSR 3.1", "fsr31")]
    [InlineData("FSR 3.1", "fsr31_12")]
    [InlineData("fsr31", "fsr31")]
    [InlineData("fsr31_12", "fsr31_12")]
    public async Task Legacy_fsr_backend_identifier_matches_successful_log(string actual, string expected)
    {
        var logs = new Logs();
        var launcher = new Launcher(() => logs.Text += $"OptiScaler v0.9.1\ninit successful for {actual}, upscaler changed\n");
        Assert.True((await new HealthCheckRunner(launcher, logs, new Clock(() => { })).RunAsync(Request() with { ExpectedBackend = expected })).Healthy);
    }

    [Fact]
    public async Task Fresh_actual_optiscaler_log_format_is_recognized()
    {
        var logs = new Logs(); var launcher = new Launcher(() => logs.Text += "[info] OptiScaler v0.9.1\n[info] init successful for FSR 3.X (ffx), upscaler changed\n");
        var result = await new HealthCheckRunner(launcher, logs, new Clock(() => { })).RunAsync(Request());
        Assert.True(result.Healthy); Assert.Equal(1, launcher.Calls);
    }
    [Fact]
    public async Task Error_line_wins_over_success_and_timeout_keeps_installation_intact()
    {
        var logs = new Logs();
        var failed = await new HealthCheckRunner(new Launcher(() => logs.Text += "OptiScaler v0.9.1\nbackend: ffx\n[error] model missing\n"), logs, new Clock(() => { })).RunAsync(Request());
        Assert.Equal(HealthCheckStatus.Failed, failed.Status);
        var timed = await new HealthCheckRunner(new Launcher(() => { }), logs, new Clock(() => { })).RunAsync(Request());
        Assert.Equal(HealthCheckStatus.TimedOut, timed.Status);
    }
    [Fact]
    public async Task Wrong_version_or_backend_never_reports_healthy()
    {
        var logs = new Logs();
        var result = await new HealthCheckRunner(new Launcher(() => logs.Text += "OptiScaler v0.8.0\nbackend: dlss\n"), logs, new Clock(() => { })).RunAsync(Request());
        Assert.False(result.Healthy);
    }
    [Fact]
    public async Task Cancellation_does_not_launch_game()
    {
        var launcher = new Launcher(() => { }); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new HealthCheckRunner(launcher, new Logs(), new Clock(() => { })).RunAsync(Request(), ct.Token));
        Assert.Equal(0, launcher.Calls);
    }
    [Fact]
    public async Task Version_survives_rotation_and_large_log_growth()
    {
        var logs = new Logs { Text = new string('o', 500) };
        var launcher = new Launcher(() => logs.Text = "OptiScaler v0.9.1\n");
        var clock = new Clock(() => logs.Text += new string('x', 10000) + "\nbackend: ffx\n");
        var result = await new HealthCheckRunner(launcher, logs, clock).RunAsync(Request());
        Assert.True(result.Healthy); Assert.True(result.LogExcerpt.Length <= Request().MaxExcerptCharacters);
    }
    [Fact]
    public async Task Error_after_large_log_preamble_is_not_missed()
    {
        var logs = new Logs();
        var launcher = new Launcher(() => logs.Text += "OptiScaler v0.9.1\nbackend: ffx\n" + new string('x', 10000) + "\n[error] failed\n");
        var result = await new HealthCheckRunner(launcher, logs, new Clock(() => { })).RunAsync(Request());
        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Creating_message_does_not_report_healthy_before_following_error()
    {
        var logs = new Logs();
        var launcher = new Launcher(() => logs.Text += "OptiScaler v0.9.1\nCreating new ffx upscaler\n");
        var clock = new Clock(() => logs.Text += "[error] initialization failed\n");
        var result = await new HealthCheckRunner(launcher, logs, clock).RunAsync(Request());
        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public void Shell_launch_can_succeed_without_process_handle()
    {
        var launcher = new ProcessGameLauncher(_ => null);
        using var game = launcher.Launch(new LaunchRequest("shell:AppsFolder\\Test!App", null, null, true));
        Assert.False(game.HasExited);
    }

    [Theory]
    [InlineData("dlss")]
    [InlineData("ffx")]
    public async Task Configuration_dump_is_not_an_initialized_backend(string backend)
    {
        var logs = new Logs();
        var launcher = new Launcher(() => logs.Text += $"OptiScaler v0.9.1\n[12:00:00] [I] Upscalers.Dx12Upscaler: {backend}\n");
        var clock = new Clock(() => logs.Text += "[E] initialization failed\n");
        var result = await new HealthCheckRunner(launcher, logs, clock).RunAsync(Request() with { ExpectedBackend = backend });
        Assert.Equal(HealthCheckStatus.Failed, result.Status);
    }

    [Fact]
    public void Non_shell_launch_without_process_handle_fails()
    {
        var launcher = new ProcessGameLauncher(_ => null);
        Assert.Throws<InvalidOperationException>(() => launcher.Launch(new LaunchRequest("game.exe", ".")));
    }
}
