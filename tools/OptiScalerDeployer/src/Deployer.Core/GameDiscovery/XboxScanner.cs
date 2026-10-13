using System.IO.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public sealed class XboxScanner : ScannerBase
{
    private readonly IXboxPackageSource _packageSource;
    private readonly string[]? _driveRoots;
    private readonly Func<string, IEnumerable<string>>? _directoryEnumerator;
    private readonly Func<string, string?>? _linkTargetResolver;
    public XboxScanner(IFileSystem fs, DirectoryWritabilityProbe probe, IXboxPackageSource packageSource,
        IEnumerable<string>? driveRoots = null, Func<string, IEnumerable<string>>? directoryEnumerator = null,
        Func<string, string?>? linkTargetResolver = null) : base(fs, probe)
    { _packageSource = packageSource; _driveRoots = driveRoots?.ToArray(); _directoryEnumerator = directoryEnumerator; _linkTargetResolver = linkTargetResolver; }
    public override string PlatformName => "Xbox";

    public override async Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct)
    {
        ClearWarnings();
        var roots = (_driveRoots ?? FixedDrives()).Select(Fs.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new List<DiscoveredGame>();
        foreach (var drive in roots)
        {
            ct.ThrowIfCancellationRequested();
            ScanXboxGames(drive, result, ct);
            if (XboxGameLibrary.TryReadGamingRoot(Fs, drive, out var customRoot, out var markerError))
                ScanCustomLibrary(customRoot!, result, ct);
            else if (markerError is not null)
                Warn($"跳过无效 .GamingRoot {Fs.Path.Combine(drive, ".GamingRoot")}：{markerError}");
            foreach (var appsRoot in WindowsAppsRoots(drive)) ScanWindowsApps(appsRoot, result, ct);
        }

        IReadOnlyList<XboxPackage> packages;
        try { packages = await _packageSource.GetCurrentUserPackagesAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Warn($"Xbox 当前用户包元数据不可用：{e.Message}"); return result; }

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();
            var location = ResolveLink(package.InstallRoot);
            var index = result.FindIndex(g => SamePath(Fs, location, g.InstallRoot));
            if (index >= 0)
            {
                var existing = result[index];
                result[index] = existing with { Title = Useful(package.DisplayName) ? package.DisplayName : existing.Title,
                    PackageFamilyName = package.PackageFamilyName, AppId = package.AppId };
                continue;
            }
            if (!HasGameEvidence(location, ct, checkShippingExecutable: true)) continue;
            var found = Create(Useful(package.DisplayName) ? package.DisplayName : Fs.Path.GetFileName(location),
                GamePlatform.Xbox, location, "Xbox package metadata", ct, package.PackageFamilyName, package.AppId);
            if (found is not null) AddUnique(result, found);
        }
        return result;
    }

    private void ScanXboxGames(string drive, List<DiscoveredGame> result, CancellationToken ct)
    {
        try
        {
            var xbox = Fs.Path.Combine(drive, "XboxGames");
            if (!DirectoryCanEnumerate(xbox, "XboxGames")) return;
            if ((Fs.File.GetAttributes(xbox) & FileAttributes.ReparsePoint) != 0) { Warn($"跳过 XboxGames 重解析目录：{xbox}"); return; }
            foreach (var gameDir in GetDirectories(xbox))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((Fs.File.GetAttributes(gameDir) & FileAttributes.ReparsePoint) != 0) continue;
                    var game = Create(Fs.Path.GetFileName(gameDir), GamePlatform.Xbox, Fs.Path.Combine(gameDir, "Content"), "XboxGames", ct);
                    if (game is not null) AddUnique(result, game);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warn($"无法扫描 Xbox 目录 {gameDir}: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warn($"无法枚举 {drive}\\XboxGames: {e.Message}"); }
    }

    private void ScanCustomLibrary(string root, List<DiscoveredGame> result, CancellationToken ct)
    {
        var drive = Fs.Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(drive))
        { Warn($"跳过不安全的 Xbox 自定义库 {root}：无法确定盘符根目录"); return; }
        if (!XboxGameLibrary.HasNoReparseAncestor(Fs, drive, root, out var reparseError))
        { Warn($"跳过不安全的 Xbox 自定义库 {root}：{reparseError}"); return; }
        try
        {
            if (!DirectoryCanEnumerate(root, "GamingRoot")) return;
            foreach (var gameDir in GetDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((Fs.File.GetAttributes(gameDir) & FileAttributes.ReparsePoint) != 0) continue;
                    var content = Fs.Path.Combine(gameDir, "Content");
                    if (!XboxGameLibrary.IsGameContent(Fs, gameDir, content, ct, Warn)) continue;
                    var game = Create(Fs.Path.GetFileName(gameDir), GamePlatform.Xbox, content, "GamingRoot", ct);
                    if (game is not null) AddUnique(result, game);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                { Warn($"无法扫描 Xbox 自定义库条目 {gameDir}: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Warn($"无法枚举 Xbox 自定义库 {root}: {e.Message}"); }
    }

    private void ScanWindowsApps(string root, List<DiscoveredGame> result, CancellationToken ct)
    {
        try
        {
            if (!DirectoryCanEnumerate(root, "WindowsApps")) return;
            foreach (var candidate in GetDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((Fs.File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0) continue;
                    var target = _linkTargetResolver is null ? Fs.DirectoryInfo.New(candidate).ResolveLinkTarget(true)?.FullName : _linkTargetResolver(candidate);
                    if (target is null || !HasGameEvidence(target, checkShippingExecutable: true) || result.Any(g => SamePath(Fs, target, g.InstallRoot))) continue;
                    var found = Create(Fs.Path.GetFileName(candidate), GamePlatform.Xbox, target, "WindowsApps junction", ct);
                    if (found is not null) AddUnique(result, found);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Warn($"无法解析 Xbox 安装链接 {candidate}: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warn($"无法枚举 WindowsApps {root}: {e.Message}"); }
    }

    private IEnumerable<string> WindowsAppsRoots(string drive)
    { yield return Fs.Path.Combine(drive, "WindowsApps"); yield return Fs.Path.Combine(drive, "Program Files", "WindowsApps"); }

    private IEnumerable<string> GetDirectories(string path) => _directoryEnumerator?.Invoke(path) ?? Fs.Directory.GetDirectories(path);

    private bool DirectoryCanEnumerate(string path, string source)
    {
        if (Fs.Directory.Exists(path)) return true;
        try { _ = Fs.DirectoryInfo.New(path).Attributes; return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException e) { Warn($"无权访问 {source} {path}: {e.Message}"); return false; }
        catch (IOException e) { Warn($"无法访问 {source} {path}: {e.Message}"); return false; }
    }

    private string ResolveLink(string path)
    { try { return Fs.DirectoryInfo.New(path).ResolveLinkTarget(true)?.FullName ?? Fs.Path.GetFullPath(path); } catch { return Fs.Path.GetFullPath(path); } }

    private bool HasGameEvidence(string path, CancellationToken ct = default, bool checkShippingExecutable = false)
    {
        try
        {
            var normalized = Fs.Path.GetFullPath(path).TrimEnd(Fs.Path.DirectorySeparatorChar, Fs.Path.AltDirectorySeparatorChar);
            var segment = normalized.Split(Fs.Path.DirectorySeparatorChar, Fs.Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("XboxGames", StringComparison.OrdinalIgnoreCase));
            if (segment || Fs.File.Exists(Fs.Path.Combine(path, "MicrosoftGame.config"))) return true;
            return checkShippingExecutable && ExecutableFinder.Find(Fs, path, ct, Warn)
                .Any(p => Fs.Path.GetFileNameWithoutExtension(p).EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> FixedDrives() => OperatingSystem.IsWindows()
        ? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)
        : [];

    private static bool SamePath(IFileSystem fs, string a, string b)
    { try { return string.Equals(GameLibrary.Normalize(fs.Path.GetFullPath(a)), GameLibrary.Normalize(fs.Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    private void AddUnique(List<DiscoveredGame> result, DiscoveredGame game)
    { if (!result.Any(existing => SamePath(Fs, existing.InstallRoot, game.InstallRoot))) result.Add(game); }
    private static bool Useful(string? value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);
}
