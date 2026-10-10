using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;

namespace Deployer.Core.Inspection;

public sealed class GameInspector : IGameInspector
{
    private readonly IFileSystem _fs;
    private readonly PeImportReader _reader;
    private readonly UpscalerSdkDetector _sdkDetector;
    private readonly Func<string, DirectoryWritability> _writabilityProbe;
    private readonly IFileVersionReader _versions;

    public GameInspector(IFileSystem fs, Func<string, DirectoryWritability>? writabilityProbe = null, IFileVersionReader? versions = null)
    {
        _fs = fs;
        _reader = new PeImportReader();
        _sdkDetector = new UpscalerSdkDetector();
        var probe = new DirectoryWritabilityProbe(fs);
        _writabilityProbe = writabilityProbe ?? probe.Probe;
        _versions = versions ?? new WindowsFileVersionReader();
    }

    public Task<InspectionReport> InspectAsync(GameTarget game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<PlanWarning>();
        var executableName = Path.GetFileName(game.ExecutableName);
        if (string.IsNullOrWhiteSpace(executableName) || !string.Equals(executableName, game.ExecutableName, StringComparison.Ordinal)
            || !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Unsupported("选中的可执行文件必须是部署目录内的文件名，不能包含路径。"));
        string deploy;
        string root;
        try { deploy = Path.GetFullPath(game.DeployDirectory); root = Path.GetFullPath(game.InstallRoot); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return Task.FromResult(Unsupported($"游戏路径无效：{e.Message}")); }
        if (!IsWithin(root, deploy)) return Task.FromResult(Unsupported("部署目录超出游戏根目录。"));
        if (HasPathSegment(root, "WindowsApps"))
            return Task.FromResult(Unsupported("当前不支持直接部署到 WindowsApps/MSIX 物理安装目录；请扫描并选择 XboxGames 中已解析的可写安装目录。"));
        var exe = Path.GetFullPath(Path.Combine(deploy, executableName));
        if (!IsWithin(deploy, exe) || !_fs.File.Exists(exe)) return Task.FromResult(Unsupported("选中的游戏可执行文件不存在或超出部署目录。"));
        var pathIssue = FindReparseIssue(Path.GetPathRoot(root)!, exe);
        if (pathIssue is not null) return Task.FromResult(Unsupported(pathIssue));

        PeImportData peData;
        try { using var stream = _fs.File.OpenRead(exe); peData = _reader.Read(stream); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException or ArgumentException or OverflowException)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking, $"无法读取有效 PE 导入信息：{e.Message}"));
            return Task.FromResult(Report(InspectionSummary.Empty, DirectoryWritability.Unknown, warnings, false));
        }

        if (peData.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking, peData.Machine == System.Reflection.PortableExecutable.Machine.I386
                ? "选中的游戏是 x86 可执行文件，当前只支持 x64 原生游戏。"
                : "选中的游戏不是受支持的 x64 原生可执行文件。"));
            return Task.FromResult(Report(InspectionSummary.Empty, DirectoryWritability.Unknown, warnings, false));
        }
        if (!peData.IsPe32Plus || peData.IsDll || !peData.IsExecutable)
            return Task.FromResult(Unsupported("选中的文件不是 PE32+ x64 原生可执行映像（可能是 DLL 或无效 exe）。"));
        if (peData.IsManaged)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking, "选中的文件是托管 .NET 程序，不是受支持的原生游戏可执行文件。"));
            return Task.FromResult(Report(InspectionSummary.Empty, DirectoryWritability.Unknown, warnings, false));
        }

        var files = EnumerateGameFiles(root, exe, deploy, cancellationToken, warnings);
        var sdks = _sdkDetector.Detect(peData.Imports, files);
        var apis = GraphicsApiDetector.Detect(peData.Imports);
        var hasEngine = _fs.Directory.Exists(_fs.Path.Combine(root, "Engine"));
        if (sdks.Count == 0) warnings.Add(new PlanWarning(WarningSeverity.Blocking, "没有检测到受支持的超分 SDK，OptiScaler 可能无法介入。"));
        if (apis.Count == 0) warnings.Add(new PlanWarning(WarningSeverity.Caution, "未从静态导入表识别出图形 API；运行时动态加载的 API 无法仅靠此检查确认。"));
        var edits = RecommendedEdits(sdks);
        DirectoryWritability writable;
        try { writable = _writabilityProbe(deploy); }
        catch { writable = DirectoryWritability.Unknown; }
        var supported = !warnings.Any(w => w.Severity == WarningSeverity.Blocking);
        var hasNativeFg = files.Concat(peData.Imports).Any(n => n.Equals("sl.dlss_g.dll", StringComparison.OrdinalIgnoreCase)
            || n.Equals("sl.dlssg.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(Report(new InspectionSummary
        {
            GraphicsApis = apis,
            DetectedSdks = sdks,
            HasEngineFolder = hasEngine,
            IsVulkanOnly = apis.Contains(GraphicsApi.Vulkan) && !apis.Contains(GraphicsApi.Dx11) && !apis.Contains(GraphicsApi.Dx12),
            HasStreamlineFrameGen = sdks.Contains(UpscalerSdk.Streamline) && hasNativeFg,
        }, writable, warnings, supported, edits));
    }

    private InspectionReport Unsupported(string message) => Report(InspectionSummary.Empty, DirectoryWritability.Unknown,
        [new PlanWarning(WarningSeverity.Blocking, message)], false);

    private static InspectionReport Report(InspectionSummary summary, DirectoryWritability writability,
        IReadOnlyList<PlanWarning> warnings, bool supported, IReadOnlyList<IniEdit>? edits = null) => new()
    {
        Summary = summary, Writability = writability, Warnings = warnings,
        RecommendedInputEdits = edits ?? Array.Empty<IniEdit>(), IsSupported = supported,
    };

    private static IReadOnlyList<IniEdit> RecommendedEdits(IReadOnlyList<UpscalerSdk> detected)
    {
        var map = new (UpscalerSdk Sdk, string Key)[]
        {
            (UpscalerSdk.Dlss, "EnableDlssInputs"), (UpscalerSdk.XeSS, "EnableXeSSInputs"),
            (UpscalerSdk.Fsr2, "EnableFsr2Inputs"), (UpscalerSdk.Fsr3, "EnableFsr3Inputs"),
            (UpscalerSdk.FfxApi, "EnableFfxInputs"),
        };
        return map.Select(x => new IniEdit("Inputs", x.Key, null,
            (detected.Contains(x.Sdk) || x.Sdk == UpscalerSdk.Dlss && detected.Contains(UpscalerSdk.Streamline)) ? "true" : "false")).ToArray();
    }

    private IReadOnlyList<string> EnumerateGameFiles(string root, string exe, string deploy, CancellationToken ct, List<PlanWarning> warnings)
    {
        var files = new List<string>();
        var rootFull = Path.GetFullPath(root);
        if (!_fs.Directory.Exists(rootFull)) return files;
        var stack = new Stack<string>(); stack.Push(rootFull);
        var visited = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (++visited > 100000) { warnings.Add(new PlanWarning(WarningSeverity.Caution, "游戏文件扫描达到安全上限，SDK 检测可能不完整。")); break; }
            if (!dir.Equals(rootFull, StringComparison.OrdinalIgnoreCase) && Path.GetRelativePath(rootFull, dir).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(p => p.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var info = _fs.DirectoryInfo.New(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (_fs.File.Exists(Path.Combine(dir, "manifest.json")) && _fs.File.Exists(Path.Combine(dir, "progress.json"))) continue;
                foreach (var file in _fs.Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFullPath(file).Equals(exe, StringComparison.OrdinalIgnoreCase))
                    {
                        // The selected executable's imports are handled directly; its filename carries no SDK signal.
                        continue;
                    }
                    var fileInfo = _fs.FileInfo.New(file);
                    if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var name = Path.GetFileName(file);
                    if (IsSdkCandidate(name))
                    {
                        try
                        {
                            if (string.Equals(_versions.GetOriginalFilename(file), "OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            warnings.Add(new PlanWarning(WarningSeverity.Caution, $"无法验证 SDK 候选文件 {file} 的原始文件名，未将它作为支持证据：{e.Message}"));
                            continue;
                        }
                    }
                    files.Add(name);
                }
                foreach (var child in _fs.Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    var full = Path.GetFullPath(child);
                    if (IsWithin(rootFull, full)) stack.Push(full);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new PlanWarning(WarningSeverity.Caution, $"无法完整扫描游戏目录 {dir}：{e.Message}"));
            }
        }
        return files;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsSdkCandidate(string name) => name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("_nvngx.dll", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sl.dlss", StringComparison.OrdinalIgnoreCase)
        || name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("libxess_dx11.dll", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ffx_fsr2_api_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ffx_fsr3upscaler_x64.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("ffx_backend_dx12_x64.dll", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || name.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase);

    private string? FindReparseIssue(string root, string path)
    {
        var rootFull = Path.GetFullPath(root); var current = Path.GetFullPath(path);
        while (IsWithin(rootFull, current))
        {
            try
            {
                var directory = _fs.Directory.Exists(current) ? _fs.DirectoryInfo.New(current) : null;
                if (directory is not null && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return "游戏路径祖先包含 reparse point，已拒绝扫描。";
                if (_fs.File.Exists(current) && (_fs.FileInfo.New(current).Attributes & FileAttributes.ReparsePoint) != 0) return "选中的 exe 是 reparse point，已拒绝扫描。";
            }
            catch (UnauthorizedAccessException) { return $"无权验证游戏路径是否包含 reparse point：{current}"; }
            catch (IOException) { return $"无法验证游戏路径是否包含 reparse point：{current}"; }
            if (current.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
        return null;
    }

    private static bool HasPathSegment(string path, string segment) => Path.GetFullPath(path)
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
}
