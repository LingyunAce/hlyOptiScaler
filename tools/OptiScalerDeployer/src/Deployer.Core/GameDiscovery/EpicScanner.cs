using System.IO.Abstractions;
using System.Text.Json;
using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public sealed class EpicScanner(IFileSystem fs, DirectoryWritabilityProbe probe, string? manifestPath = null)
    : ScannerBase(fs, probe)
{
    public override string PlatformName => "Epic";
    public override Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct)
    {
        ClearWarnings();
        ct.ThrowIfCancellationRequested();
        var result = new List<DiscoveredGame>();
        var manifest = manifestPath ?? (OperatingSystem.IsWindows()
            ? Fs.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat")
            : string.Empty);
        if (!Fs.File.Exists(manifest)) return Task.FromResult<IReadOnlyList<DiscoveredGame>>(result);
        try
        {
            using var doc = JsonDocument.Parse(Fs.File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("缺少 InstallationList 数组。");
            foreach (var item in list.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var title = Get(item, "AppName"); var path = Get(item, "InstallLocation");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(path)) continue;
                var game = Create(title, GamePlatform.Epic, path, "Epic", ct, appId: Get(item, "AppName"));
                if (game is not null) result.Add(game);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { Warn($"Epic 清单 {manifest}: {e.Message}"); }
        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(result);
    }
    private static string? Get(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

