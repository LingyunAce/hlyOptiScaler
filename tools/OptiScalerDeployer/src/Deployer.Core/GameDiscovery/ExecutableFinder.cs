using System.IO.Abstractions;

namespace Deployer.Core.GameDiscovery;

internal static class ExecutableFinder
{
    private static readonly string[] Excluded = ["launcher", "unins", "setup", "crashreportclient", "crashhandler", "crashpad", "redist", "unrealeditor", "ue4editor", "ue5editor", "shadercompileworker", "unrealpak"];

    public static IReadOnlyList<string> Find(IFileSystem fs, string root, CancellationToken ct, Action<string> warn)
    {
        var all = Walk(fs, root, ct, warn).Where(p => !Excluded.Any(word => fs.Path.GetFileNameWithoutExtension(p).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        return all.OrderBy(p => Rank(fs.Path.GetFileNameWithoutExtension(p)))
            .ThenByDescending(p => SafeLength(fs, p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int Rank(string name) => name.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase) ? 0
        : name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static IEnumerable<string> Walk(IFileSystem fs, string root, CancellationToken ct, Action<string> warn)
    {
        if (!fs.Directory.Exists(root)) yield break;
        try { if ((fs.File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) yield break; }
        catch (IOException e) { warn($"无法检查游戏目录 {root}: {e.Message}"); yield break; }
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try { files = fs.Directory.GetFiles(current); dirs = fs.Directory.GetDirectories(current); }
            catch (IOException e) { warn($"无法读取游戏目录 {current}: {e.Message}"); continue; }
            catch (UnauthorizedAccessException e) { warn($"无权读取游戏目录 {current}: {e.Message}"); continue; }
            foreach (var file in files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) yield return file;
            foreach (var dir in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(fs.Path.GetFileName(dir), "Engine", StringComparison.OrdinalIgnoreCase)) continue;
                try { if ((fs.File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) pending.Push(dir); }
                catch (IOException e) { warn($"无法检查目录 {dir}: {e.Message}"); }
                catch (UnauthorizedAccessException e) { warn($"无权检查目录 {dir}: {e.Message}"); }
            }
        }
    }

    private static long SafeLength(IFileSystem fs, string path)
    { try { return fs.FileInfo.New(path).Length; } catch { return 0; } }
}
