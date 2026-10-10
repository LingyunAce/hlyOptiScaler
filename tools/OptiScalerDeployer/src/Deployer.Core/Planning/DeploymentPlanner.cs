using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Profiles;
using Deployer.Core.Util;

namespace Deployer.Core.Planning;

/// <summary>Builds an immutable, ordered plan. It reads files but never mutates the file system.</summary>
public sealed class DeploymentPlanner : IDeploymentPlanner
{
    private readonly IFileSystem _fs;
    private readonly IDllIdentitySelector _identitySelector;
    private readonly PayloadWalker _walker;
    private readonly IIniPatcher _iniPatcher;

    public DeploymentPlanner(
        IFileSystem fs, IDllIdentitySelector identitySelector, PayloadWalker walker, IIniPatcher iniPatcher)
    {
        _fs = fs;
        _identitySelector = identitySelector;
        _walker = walker;
        _iniPatcher = iniPatcher;
    }

    public DeploymentPlan Plan(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Game);
        ArgumentNullException.ThrowIfNull(request.Payload);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Game.DeployDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Payload.RootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BackupRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Game.ExecutableName);

        var deployDirectory = _fs.Path.GetFullPath(request.Game.DeployDirectory);
        var payloadRoot = _fs.Path.GetFullPath(request.Payload.RootDirectory);
        var fingerprint = request.Game.Fingerprint;
        if (fingerprint.Length != 8 || fingerprint.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException("Game fingerprint 必须是 8 位十六进制字符串", nameof(request));
        }

        var game = request.Game with { DeployDirectory = deployDirectory, Fingerprint = fingerprint.ToLowerInvariant() };
        var planId = request.PlanId ?? Guid.NewGuid();
        var identity = _identitySelector.Select(new IdentityContext(
            game.Platform, request.Inspection.IsVulkanOnly, deployDirectory,
            request.AllowOverwriteGameFile, request.PreferAsiIdentity));
        var backupDirectory = _fs.Path.GetFullPath(_fs.Path.Combine(
            request.BackupRoot, game.Fingerprint, planId.ToString("D")));

        var writes = new List<FileWrite>();
        var backups = new List<BackupEntry>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in _walker.Walk(payloadRoot))
        {
            var destination = SafeDestination(deployDirectory, file.RelativePath);
            AddWrite(writes, destinations, new FileWrite(file.FullPath, destination, file.Length, file.Sha256));
            AddBackupIfExisting(backups, destination, file.RelativePath, backupDirectory, BackupReason.OverwriteGameFile);
        }

        var iniSource = SafeSource(payloadRoot, "OptiScaler.ini");
        var sourceIni = _fs.File.ReadAllText(iniSource);
        var patched = _iniPatcher.Patch(sourceIni, BuildIniEdits(request, game, sourceIni));
        var resolvedEdits = patched.AppliedEdits.Concat(patched.AppendedEdits).ToArray();
        var iniContent = patched.Content;
        var iniDestination = SafeDestination(deployDirectory, "OptiScaler.ini");
        AddWrite(writes, destinations, new FileWrite(
            iniSource, iniDestination, Encoding.UTF8.GetByteCount(iniContent), Hashing.Sha256OfString(iniContent), iniContent));
        AddBackupIfExisting(backups, iniDestination, "OptiScaler.ini", backupDirectory, BackupReason.IniModified);

        var identitySource = SafeSource(payloadRoot, "OptiScaler.dll");
        var identityDestination = SafeDestination(deployDirectory, identity.FileName);
        var identityInfo = _fs.FileInfo.New(identitySource);
        AddWrite(writes, destinations, new FileWrite(
            identitySource, identityDestination, identityInfo.Length, Hashing.Sha256OfFile(_fs, identitySource)));
        if (identity.ExistingFileReason is { } reason)
        {
            AddBackupIfExisting(backups, identityDestination, identity.FileName, backupDirectory, reason);
        }

        return new DeploymentPlan
        {
            PlanId = planId,
            Game = game,
            Payload = PayloadInfo.From(request.Payload with { RootDirectory = payloadRoot }),
            Identity = identity,
            Profile = request.Profile,
            Writability = request.Writability,
            Elevation = MapElevation(request.Writability),
            Writes = writes,
            Backups = backups,
            IniEdits = resolvedEdits,
            Warnings = BuildWarnings(request, game, identity, backups, patched.AppendedEdits),
        };
    }

    private static IReadOnlyList<IniEdit> BuildIniEdits(PlanRequest request, GameTarget game, string sourceIni)
    {
        var edits = new List<IniEdit>(request.Profile.Edits);
        // Older official releases call the FidelityFX backend fsr31; only adapt when
        // the payload advertises that schema. Other sections contain unrelated ffx API links.
        var advertised = Regex.Match(sourceIni, @"(?ims)^\s*\[Upscalers\][^\r\n]*\r?\n(.*?)(?=^\s*\[|\z)").Groups[1].Value;
        bool HasBackend(string name) => Regex.IsMatch(advertised, @"(?i)(?<![a-z0-9_])" + Regex.Escape(name) + @"(?![a-z0-9_])");
        if (HasBackend("fsr31") && !HasBackend("ffx") && !HasBackend("ffx_12"))
        {
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (!edit.Section.Equals("Upscalers", StringComparison.OrdinalIgnoreCase)) continue;
                var alias = edit.NewValue.ToLowerInvariant() switch { "ffx" => "fsr31", "ffx_12" => "fsr31_12", _ => null };
                if (alias is null) continue;
                if (!HasBackend(alias)) throw new PayloadNotFoundException($"所选 payload 未声明支持 {alias}；请选择兼容的 payload 或 Profile。");
                edits[i] = edit with { NewValue = alias };
            }
        }
        foreach (var inputEdit in request.RecommendedInputEdits)
        {
            edits.RemoveAll(edit => string.Equals(edit.Section, inputEdit.Section, StringComparison.OrdinalIgnoreCase)
                && string.Equals(edit.Key, inputEdit.Key, StringComparison.OrdinalIgnoreCase));
            edits.Add(inputEdit);
        }
        if (request.EnableFrameGen)
        {
            var useStreamline = request.Inspection.HasStreamlineFrameGen
                ?? request.Inspection.DetectedSdks.Contains(UpscalerSdk.Streamline);
            edits.AddRange(BuiltinProfiles.FrameGenAddOn(useStreamline));
        }

        edits.Add(new IniEdit("ProcessFilter", "TargetProcessName", null, Path.GetFileName(game.ExecutableName)));
        edits.Add(new IniEdit("Log", "LogToFile", null, request.EnableLogging ? "true" : "false"));
        edits.Add(new IniEdit("Log", "LogLevel", null, "2"));
        return edits;
    }

    private string SafeSource(string payloadRoot, string relativePath)
    {
        var source = _fs.Path.GetFullPath(_fs.Path.Combine(payloadRoot, relativePath));
        if (!IsContained(payloadRoot, source) || !_fs.File.Exists(source))
        {
            throw new PayloadNotFoundException($"payload 缺少必需文件或路径越界：{relativePath}");
        }

        return source;
    }

    private string SafeDestination(string deployDirectory, string relativePath)
    {
        if (_fs.Path.IsPathRooted(relativePath)
            || relativePath.Split(new[] { '\\', '/' }).Any(part => part is ".." or "."))
        {
            throw new InvalidDataException($"payload 相对路径无效：{relativePath}");
        }

        var destination = _fs.Path.GetFullPath(_fs.Path.Combine(deployDirectory, relativePath));
        if (!IsContained(deployDirectory, destination))
        {
            throw new InvalidDataException($"payload 目标路径超出部署目录：{relativePath}");
        }

        return destination;
    }

    private bool IsContained(string root, string candidate)
    {
        var normalizedRoot = _fs.Path.GetFullPath(root);
        var separator = _fs.Path.DirectorySeparatorChar;
        var prefix = normalizedRoot.EndsWith(separator) ? normalizedRoot : normalizedRoot + separator;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private string SafeBackupDestination(string backupDirectory, string relativePath)
    {
        var destination = _fs.Path.GetFullPath(_fs.Path.Combine(backupDirectory, relativePath));
        if (!IsContained(backupDirectory, destination))
        {
            throw new InvalidDataException($"备份相对路径无效：{relativePath}");
        }

        return destination;
    }

    private static void AddWrite(List<FileWrite> writes, HashSet<string> destinations, FileWrite write)
    {
        if (!destinations.Add(write.DestinationPath))
        {
            throw new InvalidDataException($"多个 payload 文件映射到同一目标：{write.DestinationPath}");
        }

        writes.Add(write);
    }

    private void AddBackupIfExisting(
        List<BackupEntry> backups,
        string destination,
        string relativePath,
        string backupDirectory,
        BackupReason reason)
    {
        if (!_fs.File.Exists(destination)
            || backups.Any(entry => string.Equals(entry.OriginalPath, destination, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        backups.Add(new BackupEntry(destination, SafeBackupDestination(backupDirectory, relativePath), reason));
    }

    private static ElevationRequirement MapElevation(DirectoryWritability writability) => writability switch
    {
        DirectoryWritability.Writable or DirectoryWritability.Unknown => ElevationRequirement.NotRequired,
        DirectoryWritability.NeedsElevation => ElevationRequirement.Required,
        DirectoryWritability.Blocked => ElevationRequirement.Blocked,
        _ => ElevationRequirement.NotRequired,
    };

    private static IReadOnlyList<PlanWarning> BuildWarnings(
        PlanRequest request,
        GameTarget game,
        IdentityDecision identity,
        IReadOnlyList<BackupEntry> backups,
        IReadOnlyList<IniEdit> appendedEdits)
    {
        var warnings = new List<PlanWarning>();
        warnings.AddRange(request.InspectionWarnings);
        if (!request.Inspection.HasAnyUpscalerSdk)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking,
                $"{game.Title} 里没找到任何超分 SDK（DLSS / XeSS / FSR）。OptiScaler 没有现成调用可以介入，安装不会生效。"));
        }

        if (identity.Status == IdentityStatus.RequiresUserDecision)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking,
                identity.UserPromptMessage ?? "身份 DLL 与游戏文件冲突，必须先选择处理方式。"));
        }
        else if (identity.SkippedDueToConflict.Count > 0 && identity.UserPromptMessage is { } skipMessage)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Info, skipMessage));
        }

        if (identity.Chosen == DllIdentity.Asi)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                "选择了 OptiScaler.asi。只有游戏侧已有 ASI Loader 时才会加载，否则不会生效。"));
        }

        if (request.Writability == DirectoryWritability.NeedsElevation)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                $"{game.DeployDirectory} 需要管理员权限才能写入。"));
        }
        else if (request.Writability == DirectoryWritability.Blocked)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Blocking,
                $"{game.DeployDirectory} 无法写入，即使提权也不行。"));
        }

        if (request.Inspection.HasEngineFolder
            && !game.DeployDirectory.Replace('/', '\\').EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                $"检测到 UE 游戏，但部署目录不是 Binaries\\Win64：{game.DeployDirectory}。"));
        }

        if (HasInterop(request.Profile, request.Inspection, GraphicsApi.Dx11, "Dx11Upscaler"))
        {
            warnings.Add(new PlanWarning(WarningSeverity.Info,
                "DX11 当前通过 D3D11on12 interop 使用 FSR4，约有 10% 性能损失。"));
        }

        if (HasInterop(request.Profile, request.Inspection, GraphicsApi.Vulkan, "VulkanUpscaler"))
        {
            warnings.Add(new PlanWarning(WarningSeverity.Info,
                "Vulkan 当前通过 DX12 interop 使用 FSR4，约有 10% 性能损失。"));
        }

        var gameFileCount = backups.Count(entry => entry.Reason == BackupReason.OverwriteGameFile);
        if (gameFileCount > 0)
        {
            warnings.Add(new PlanWarning(WarningSeverity.Caution,
                $"将覆盖 {gameFileCount} 个已有游戏文件；原件会先存入影子备份。"));
        }

        if (appendedEdits.Count > 0)
        {
            var keys = string.Join("、", appendedEdits.Select(edit => $"{edit.Section}.{edit.Key}"));
            warnings.Add(new PlanWarning(WarningSeverity.Info,
                $"payload 的 OptiScaler.ini 缺少 {appendedEdits.Count} 个设置项，计划将在对应 section 追加：{keys}。"));
        }

        return warnings;
    }

    private static bool HasInterop(
        UpscalerProfile profile, InspectionSummary inspection, GraphicsApi api, string key)
    {
        if (!inspection.GraphicsApis.Contains(api))
        {
            return false;
        }

        return profile.Edits.Any(edit =>
            string.Equals(edit.Section, "Upscalers", StringComparison.OrdinalIgnoreCase)
            && string.Equals(edit.Key, key, StringComparison.OrdinalIgnoreCase)
            && edit.NewValue.EndsWith("_12", StringComparison.Ordinal));
    }
}
