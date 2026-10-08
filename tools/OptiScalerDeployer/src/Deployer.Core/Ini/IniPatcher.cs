using System.Text;
using Deployer.Core.Domain;

namespace Deployer.Core.Ini;

public sealed class IniPatcher : IIniPatcher
{
    private const string AddedByDeployer = "; added by OptiScaler Deployer";

    public IniPatchResult Patch(string originalContent, IReadOnlyList<IniEdit> edits)
    {
        var newLine = DetectNewLine(originalContent);
        var lines = SplitLines(originalContent, newLine);

        if (edits.Count == 0)
        {
            return new IniPatchResult(originalContent, Array.Empty<IniEdit>(), Array.Empty<IniEdit>(), newLine);
        }

        // 后出现的 edit 覆盖先出现的（"last one wins"），但顺序按**首次出现**的位置保留。
        // 不能直接靠 pending.Values 的枚举顺序：Dictionary 一旦 Remove 过就不再保证插入序。
        var pending = new Dictionary<(string Section, string Key), IniEdit>(KeyComparer.Instance);
        var orderedKeys = new List<(string Section, string Key)>();
        foreach (var edit in edits)
        {
            var key = (edit.Section, edit.Key);
            if (!pending.ContainsKey(key))
            {
                orderedKeys.Add(key);
            }

            pending[key] = edit;
        }

        var applied = new List<IniEdit>();
        var sectionLastLineIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;   // 注释行：原样保留，绝不参与解析
            }

            if (trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']'))
            {
                if (currentSection.Length > 0)
                {
                    sectionLastLineIndex[currentSection] = LastContentLineIndex(lines, i - 1);
                }

                currentSection = trimmed.TrimEnd()[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;   // 空行或畸形行：原样保留
            }

            var key = line[..eq].Trim();
            if (!pending.TryGetValue((currentSection, key), out var edit))
            {
                continue;
            }

            lines[i] = line[..eq] + "=" + edit.NewValue;   // 只替换 = 右侧，左半部分（含原有空格）原样保留
            applied.Add(edit.WithOldValue(line[(eq + 1)..].Trim()));
            pending.Remove((currentSection, key));
        }

        if (currentSection.Length > 0)
        {
            sectionLastLineIndex[currentSection] = LastContentLineIndex(lines, lines.Count - 1);
        }

        var appended = new List<IniEdit>();
        var createdSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in orderedKeys)
        {
            if (!pending.TryGetValue(key, out var edit))
            {
                continue;   // 已在上面就地替换过
            }

            if (sectionLastLineIndex.TryGetValue(edit.Section, out var insertAt) && insertAt >= 0)
            {
                lines.Insert(insertAt + 1, edit.Key + "=" + edit.NewValue);
                // 插入点下移一行，同一 section 的下一条接着排在它后面
                sectionLastLineIndex[edit.Section] = insertAt + 1;
            }
            else
            {
                if (createdSections.Add(edit.Section))
                {
                    if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                    {
                        lines.Add(string.Empty);
                    }

                    lines.Add(AddedByDeployer);
                    lines.Add("[" + edit.Section + "]");
                }

                lines.Add(edit.Key + "=" + edit.NewValue);
                sectionLastLineIndex[edit.Section] = lines.Count - 1;
            }

            appended.Add(edit);
        }

        return new IniPatchResult(Join(lines, newLine), applied, appended, newLine);
    }

    /// <summary>从 <paramref name="from"/> 往前找最后一个非空行。避免把新键插在 section 末尾的空行之后。</summary>
    private static int LastContentLineIndex(List<string> lines, int from)
    {
        while (from >= 0 && lines[from].Trim().Length == 0)
        {
            from--;
        }

        return from;
    }

    private static string DetectNewLine(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static List<string> SplitLines(string content, string newLine)
    {
        var parts = content.Split(new[] { newLine }, StringSplitOptions.None).ToList();
        // 文件以换行结尾时 Split 会多出一个空尾项；去掉它，Join 时统一补回
        if (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    private static string Join(List<string> lines, string newLine)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append(newLine);
        }

        return sb.ToString();
    }

    private sealed class KeyComparer : IEqualityComparer<(string Section, string Key)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((string Section, string Key) x, (string Section, string Key) y) =>
            string.Equals(x.Section, y.Section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Section, string Key) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}
