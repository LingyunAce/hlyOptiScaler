using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Deployer.Core.Domain;
using Deployer.Core.Elevation;

namespace Deployer.Core.HealthCheck;

public sealed class HealthCheckRunner : IHealthCheckRunner
{
    private readonly IGameLauncher _launcher;
    private readonly IHealthLogReader _logs;
    private readonly IHealthClock _clock;

    public HealthCheckRunner(IGameLauncher launcher, IHealthLogReader logs, IHealthClock? clock = null)
    {
        _launcher = launcher; _logs = logs; _clock = clock ?? new SystemHealthClock();
    }

    public async Task<HealthCheckResult> RunAsync(HealthCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(10) || request.PollMilliseconds <= 0 || request.MaxExcerptCharacters is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(request));
        var logPath = Path.Combine(request.Game.DeployDirectory, "OptiScaler.log");
        ElevationPolicy.ValidateTarget(logPath, request.Game.InstallRoot);
        var baseline = request.LogStartOffset ?? SafeLength(logPath);
        var prefix = SafeRead(logPath, 0, 128);
        IRunningGame? game;
        try { game = request.LaunchGame ? _launcher.Launch(BuildLaunch(request.Game)) : null; }
        catch (Exception ex) { return new(HealthCheckStatus.LaunchFailed, null, null, string.Empty, ex.Message, false); }
        using (game)
        {
            var deadline = _clock.UtcNow + request.Timeout;
            var excerpt = string.Empty;
            var cursor = baseline;
            string? version = null, backend = null;
            while (_clock.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = TryLength(logPath);
                if (length is null) { await _clock.DelayAsync(TimeSpan.FromMilliseconds(request.PollMilliseconds), cancellationToken); continue; }
                var currentPrefix = prefix.Length == 0 ? "" : SafeRead(logPath, 0, Encoding.UTF8.GetByteCount(prefix));
                if (length < cursor || (prefix.Length > 0 && currentPrefix.Length > 0 && currentPrefix != prefix))
                { baseline = 0; cursor = 0; version = null; backend = null; excerpt = ""; prefix = SafeRead(logPath, 0, 128); }
                HealthLogChunk chunk;
                try { chunk = _logs.ReadChunk(logPath, cursor, request.MaxExcerptCharacters); }
                catch { await _clock.DelayAsync(TimeSpan.FromMilliseconds(request.PollMilliseconds), cancellationToken); continue; }
                var parsed = Parse(excerpt + chunk.Text);
                cursor = chunk.NextOffset;
                excerpt = Trim(excerpt + chunk.Text, request.MaxExcerptCharacters);
                version = parsed.Version ?? version; backend = parsed.Backend ?? backend;
                if (parsed.Error) return new HealthCheckResult(HealthCheckStatus.Failed, parsed.Version, parsed.Backend, Trim(excerpt, request.MaxExcerptCharacters), "OptiScaler reported an error.", false) { LogStartOffset = baseline };
                if (cursor < length && chunk.Text.Length > 0) continue;
                if (version is not null && backend is not null
                    && VersionMatches(version, request.ExpectedVersion)
                    && BackendMatches(backend, request.ExpectedBackend))
                    return new HealthCheckResult(HealthCheckStatus.Healthy, version, backend, excerpt, null, false) { LogStartOffset = baseline };
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(request.PollMilliseconds), cancellationToken).ConfigureAwait(false);
            }
            return new HealthCheckResult(HealthCheckStatus.TimedOut, version, backend, excerpt, "No fresh healthy startup log was observed before timeout.", true) { LogStartOffset = baseline };
        }
    }

    private static LaunchRequest BuildLaunch(GameTarget game)
    {
        if (game.Platform == GamePlatform.Steam)
        {
            if (string.IsNullOrWhiteSpace(game.AppId) || !Regex.IsMatch(game.AppId, @"^[0-9]+$") || !uint.TryParse(game.AppId, out var appId) || appId == 0)
                throw new InvalidOperationException("Steam launch metadata is invalid.");
            return new($"steam://run/{appId}", null, null, true);
        }
        if (game.Platform == GamePlatform.Xbox)
        {
            if (string.IsNullOrWhiteSpace(game.PackageFamilyName) || string.IsNullOrWhiteSpace(game.AppId)
                || !Regex.IsMatch(game.PackageFamilyName, @"^[A-Za-z0-9._-]+$") || !Regex.IsMatch(game.AppId, @"^[A-Za-z0-9._-]+$")) throw new InvalidOperationException("Xbox launch metadata is invalid.");
            return new($"shell:AppsFolder\\{game.PackageFamilyName}!{game.AppId}", null, null, true);
        }
        var exe = Path.Combine(game.DeployDirectory, game.ExecutableName);
        if (game.ExecutableName != Path.GetFileName(game.ExecutableName) || !game.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid executable name.");
        ElevationPolicy.ValidateTarget(exe, game.DeployDirectory);
        return new(exe, game.DeployDirectory);
    }

    private long SafeLength(string path) { try { return _logs.Length(path); } catch { return 0; } }
    private long? TryLength(string path) { try { return _logs.Length(path); } catch { return null; } }
    private string SafeRead(string path, long offset, int max)
    {
        try
        {
            return _logs.ReadFrom(path, offset, max);
        }
        catch { return string.Empty; }
    }
    private static string Trim(string value, int max) => value.Length <= max ? value : value[^max..];
    private static bool BackendMatches(string actual, string expected)
    {
        if (expected.Equals("fsr31", StringComparison.OrdinalIgnoreCase) || expected.Equals("fsr31_12", StringComparison.OrdinalIgnoreCase)
            || expected.Equals("FSR 3.1", StringComparison.OrdinalIgnoreCase))
            return actual.Equals("fsr31", StringComparison.OrdinalIgnoreCase) || actual.Equals("fsr31_12", StringComparison.OrdinalIgnoreCase)
                || actual.Contains("FSR 3.1", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(expected) && actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
    private static bool VersionMatches(string actual, string expected)
    {
        expected = expected.TrimStart('v').Split('-')[0];
        if (Version.TryParse(actual, out var a) && Version.TryParse(expected, out var e))
            return a.Major == e.Major && a.Minor == e.Minor && a.Build == e.Build && Math.Max(0, a.Revision) == Math.Max(0, e.Revision);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    private static Parsed Parse(string text)
    {
        var version = Regex.Match(text, @"(?im)OptiScaler[^\r\n]*?(?:version|v)\s*[:=]?\s*([0-9]+(?:\.[0-9]+)+)").Groups[1].Value;
        var backend = Regex.Match(text, @"(?im)^(?:\[[^\r\n\]]+\]\s*)*(?:output\s+upscaler|upscaler|backend)\s*[:=]\s*([^\r\n]+)").Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(backend)) backend = Regex.Match(text, @"(?im)init successful for\s+([^\r\n,]+),\s*upscaler changed").Groups[1].Value.Trim();
        var error = Regex.IsMatch(text, @"(?im)\[(?:error|fatal|critical|E|C)\]|^\S+\t(?:E|C)\t|\b(error|fatal)\s*[:=]|amdxc64\.dll.*failed|model.*missing");
        return new(string.IsNullOrWhiteSpace(version) ? null : version, string.IsNullOrWhiteSpace(backend) ? null : backend, error);
    }
    private readonly record struct Parsed(string? Version, string? Backend, bool Error);
    private sealed class SystemHealthClock : IHealthClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct); }
}

public sealed class ProcessGameLauncher : IGameLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    public ProcessGameLauncher(Func<ProcessStartInfo, Process?>? start = null) => _start = start ?? Process.Start;

    public IRunningGame Launch(LaunchRequest request)
    {
        var psi = new ProcessStartInfo(request.Executable) { UseShellExecute = request.IsShellUri, WorkingDirectory = request.WorkingDirectory ?? string.Empty, Arguments = request.Arguments ?? string.Empty };
        var process = _start(psi);
        if (process is null)
        {
            if (request.IsShellUri) return NoProcessRunningGame.Instance;
            throw new InvalidOperationException("Game process could not be started.");
        }
        return new ProcessRunningGame(process);
    }
    private sealed class ProcessRunningGame(Process process) : IRunningGame { public bool HasExited => process.HasExited; public void Dispose() => process.Dispose(); }
    private sealed class NoProcessRunningGame : IRunningGame
    {
        public static readonly NoProcessRunningGame Instance = new();
        public bool HasExited => false;
        public void Dispose() { }
    }
}

public sealed class FileHealthLogReader : IHealthLogReader
{
    public long Length(string path) => new FileInfo(path).Length;
    public string ReadFrom(string path, long offset, int maxCharacters)
        => ReadChunk(path, offset, maxCharacters).Text;
    public HealthLogChunk ReadChunk(string path, long offset, int maxCharacters)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        var buffer = new byte[maxCharacters]; var count = stream.Read(buffer, 0, buffer.Length);
        return new(Encoding.UTF8.GetString(buffer, 0, count), stream.Position);
    }
}
