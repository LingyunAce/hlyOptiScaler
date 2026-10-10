using Deployer.Core.Domain;

namespace Deployer.Core.HealthCheck;

public enum HealthCheckStatus { Healthy, Failed, TimedOut, LaunchFailed }

public sealed record HealthCheckRequest
{
    public required GameTarget Game { get; init; }
    public required string ExpectedVersion { get; init; }
    public required string ExpectedBackend { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public int PollMilliseconds { get; init; } = 500;
    public int MaxExcerptCharacters { get; init; } = 8000;
    public bool LaunchGame { get; init; } = true;
    public long? LogStartOffset { get; init; }
}

public sealed record HealthCheckResult(
    HealthCheckStatus Status,
    string? ActualVersion,
    string? ActualBackend,
    string LogExcerpt,
    string? ErrorMessage,
    bool IsTimedOut)
{
    public bool Healthy => Status == HealthCheckStatus.Healthy;
    public long LogStartOffset { get; init; }
}

public sealed record LaunchRequest(string Executable, string? WorkingDirectory, string? Arguments = null, bool IsShellUri = false);

public interface IGameLauncher
{
    IRunningGame Launch(LaunchRequest request);
}

public interface IRunningGame : IDisposable
{
    bool HasExited { get; }
}

public interface IHealthClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IHealthLogReader
{
    long Length(string path);
    string ReadFrom(string path, long offset, int maxCharacters);
    HealthLogChunk ReadChunk(string path, long offset, int maxCharacters)
    {
        var text = ReadFrom(path, offset, maxCharacters);
        return new(text, offset + System.Text.Encoding.UTF8.GetByteCount(text));
    }
}
public sealed record HealthLogChunk(string Text, long NextOffset);

public interface IHealthCheckRunner
{
    Task<HealthCheckResult> RunAsync(HealthCheckRequest request, CancellationToken cancellationToken = default);
}
