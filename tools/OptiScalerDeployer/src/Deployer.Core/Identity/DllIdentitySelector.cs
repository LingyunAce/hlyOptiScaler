using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Identity;

public sealed class DllIdentitySelector : IDllIdentitySelector
{
    private const string OptiScalerOriginalFilename = "OptiScaler.dll";

    public static IReadOnlyList<DllIdentity> XboxPriority { get; } = new[]
    {
        DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp,
        DllIdentity.Wininet, DllIdentity.Winhttp, DllIdentity.Dxgi, DllIdentity.D3d12,
    };

    public static IReadOnlyList<DllIdentity> VulkanPriority { get; } = new[]
    {
        DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp, DllIdentity.Dxgi,
    };

    public static IReadOnlyList<DllIdentity> DefaultPriority { get; } = new[]
    {
        DllIdentity.Dxgi, DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp, DllIdentity.D3d12,
    };

    private static readonly IReadOnlyList<DllIdentity> NoSkipped = Array.Empty<DllIdentity>();
    private readonly IFileSystem _fs;
    private readonly IFileVersionReader _versions;

    public DllIdentitySelector(IFileSystem fs, IFileVersionReader versions)
    {
        _fs = fs;
        _versions = versions;
    }

    public static IReadOnlyList<DllIdentity> PriorityFor(GamePlatform platform, bool isVulkanOnly) =>
        isVulkanOnly ? VulkanPriority : platform == GamePlatform.Xbox ? XboxPriority : DefaultPriority;

    public IdentityDecision Select(IdentityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.DeployDirectory);

        if (context.PreferAsiIdentity)
        {
            return EvaluateSingle(context, DllIdentity.Asi);
        }

        var priority = PriorityFor(context.Platform, context.IsVulkanOnly);
        var skipped = new List<DllIdentity>();
        foreach (var candidate in priority)
        {
            switch (Classify(context.DeployDirectory, candidate))
            {
                case SlotVerdict.Free:
                    return Resolved(candidate, skipped, null, null);
                case SlotVerdict.PreviousOptiScaler:
                    return Resolved(candidate, skipped, BackupReason.OverwritePreviousOpti, null);
                case SlotVerdict.GameOwnFile:
                    skipped.Add(candidate);
                    break;
            }
        }

        var provisional = priority[0];
        var prompt = BuildConflictPrompt(context.DeployDirectory, skipped);
        if (!context.AllowOverwriteGameFile)
        {
            return new IdentityDecision(
                provisional, provisional.FileName(), IdentityStatus.RequiresUserDecision,
                skipped, BackupReason.OverwriteGameFile, prompt);
        }

        var skippedOtherCandidates = skipped.Where(candidate => candidate != provisional).ToArray();
        return Resolved(
            provisional,
            skippedOtherCandidates,
            BackupReason.OverwriteGameFile,
            $"已按你的设置选择覆盖 {provisional.FileName()}；原件会先保存到影子备份。"
            + "OptiScaler proxy 会转发到 System32 的原生 DLL，游戏自定义导出可能无法继续使用。");
    }

    private IdentityDecision EvaluateSingle(IdentityContext context, DllIdentity identity) =>
        Classify(context.DeployDirectory, identity) switch
        {
            SlotVerdict.Free => Resolved(identity, NoSkipped, null, null),
            SlotVerdict.PreviousOptiScaler =>
                Resolved(identity, NoSkipped, BackupReason.OverwritePreviousOpti, null),
            _ => new IdentityDecision(
                identity,
                identity.FileName(),
                context.AllowOverwriteGameFile ? IdentityStatus.Resolved : IdentityStatus.RequiresUserDecision,
                NoSkipped,
                BackupReason.OverwriteGameFile,
                $"{identity.FileName()} 已存在且不是 OptiScaler。覆盖时原件会先保存到影子备份。"),
        };

    private SlotVerdict Classify(string deployDirectory, DllIdentity identity)
    {
        var path = _fs.Path.Combine(deployDirectory, identity.FileName());
        if (!_fs.File.Exists(path))
        {
            return SlotVerdict.Free;
        }

        return string.Equals(
            _versions.GetOriginalFilename(path), OptiScalerOriginalFilename, StringComparison.OrdinalIgnoreCase)
            ? SlotVerdict.PreviousOptiScaler
            : SlotVerdict.GameOwnFile;
    }

    private static IdentityDecision Resolved(
        DllIdentity chosen,
        IReadOnlyList<DllIdentity> skipped,
        BackupReason? existingFileReason,
        string? prompt)
    {
        var message = prompt;
        if (message is null && skipped.Count > 0)
        {
            message = $"因与游戏自带文件冲突，已跳过 {string.Join("、", skipped.Select(x => x.FileName()))}，改用 {chosen.FileName()}。";
        }

        return new IdentityDecision(
            chosen, chosen.FileName(), IdentityStatus.Resolved, skipped, existingFileReason, message);
    }

    private static string BuildConflictPrompt(string directory, IReadOnlyList<DllIdentity> conflicts)
    {
        var names = string.Join("、", conflicts.Select(x => x.FileName()));
        return $"{directory} 里的 {names} 都是游戏自带文件。覆盖后 OptiScaler proxy 会转发到 System32 的原生 DLL，游戏自定义导出可能丢失。"
             + "你可以覆盖并备份、改用 OptiScaler.asi（需要游戏已有 ASI Loader，通常不推荐），或取消。";
    }

    private enum SlotVerdict { Free, PreviousOptiScaler, GameOwnFile }
}
