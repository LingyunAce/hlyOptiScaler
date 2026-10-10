using System.IO.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Persistence;

/// <summary>Applies only scan conclusions supported by an error-free scan of the matching deploy directory.</summary>
public sealed class InstallLedgerReconciler(IFileSystem fs, IInstallLedger ledger)
{
    public ReconcileResult MarkMissingIdentities(
        IReadOnlyList<DiscoveredGame> discoveredGames,
        IReadOnlyList<string> scanWarnings)
    {
        ArgumentNullException.ThrowIfNull(discoveredGames);
        ArgumentNullException.ThrowIfNull(scanWarnings);
        if (scanWarnings.Count > 0) return new ReconcileResult(0, ["扫描源有警告，未根据不完整结果标记 NeedsReinstall。"]);

        var records = ledger.All();
        var changed = 0;
        var warnings = new List<string>();
        foreach (var record in records)
        {
            if (record.State is InstallState.RolledBack or InstallState.NeedsReinstall or InstallState.Failed) continue;
            string deployPath;
            try
            {
                if (string.IsNullOrWhiteSpace(record.IdentityFileName)
                    || !string.Equals(fs.Path.GetFileName(record.IdentityFileName), record.IdentityFileName, StringComparison.Ordinal))
                {
                    warnings.Add($"台账记录 {record.Fingerprint} 的身份 DLL 文件名无效，未进行缺失判断。");
                    continue;
                }

                deployPath = fs.Path.GetFullPath(record.DeployDirectory);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"台账记录 {record.Fingerprint} 的部署目录无效，未进行缺失判断：{e.Message}");
                continue;
            }

            bool matched;
            try
            {
                matched = discoveredGames.Any(game => game.Writability == DirectoryWritability.Writable
                    && game.ExecutableCandidates.Any(executable => string.Equals(
                        fs.Path.GetFullPath(fs.Path.GetDirectoryName(executable) ?? string.Empty),
                        deployPath, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"无法核对台账记录 {record.Fingerprint} 的已发现程序路径：{e.Message}");
                continue;
            }
            if (!matched) continue;

            IReadOnlyCollection<string> files;
            try
            {
                if (HasReparseAncestor(deployPath)
                    || !discoveredGames.Where(game => game.Writability == DirectoryWritability.Writable)
                        .SelectMany(game => game.ExecutableCandidates)
                        .Any(executable => string.Equals(fs.Path.GetDirectoryName(executable), record.DeployDirectory, StringComparison.OrdinalIgnoreCase)
                            && fs.File.Exists(executable)))
                {
                    continue;
                }

                files = fs.Directory.GetFiles(deployPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法确认 {record.GameTitle} 的身份 DLL 状态，未改台账：{e.Message}");
                continue;
            }

            if (files.Any(path => string.Equals(fs.Path.GetFileName(path), record.IdentityFileName, StringComparison.OrdinalIgnoreCase))) continue;
            if (ledger.SetState(record.Fingerprint, InstallState.NeedsReinstall)) changed++;
        }

        return new ReconcileResult(changed, warnings);
    }

    private bool HasReparseAncestor(string directory)
    {
        var current = fs.DirectoryInfo.New(fs.Path.GetFullPath(directory));
        while (current is not null)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current.Parent;
        }

        return false;
    }
}

public sealed record ReconcileResult(int MarkedNeedsReinstall, IReadOnlyList<string> Warnings);
