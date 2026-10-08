using System.Text;
using Deployer.Core.Domain;

namespace Deployer.Core.Ini;

/// <remarks>
/// 后来者最容易搞错的三件事，都写在实现之前：
/// 1. **不重新序列化**是设计文档 §8.2 的硬约束，不是风格偏好。真 OptiScaler.ini
///    共 1571 行，其中 864 行（55%）是注释；任何现成 INI 库（SimpleIni / IniParser）
///    在解析-回写时都会把注释整片丢掉。所以这里坚持逐行文本改写，绝不 round-trip。
/// 2. **section 作用域匹配是硬性要求**。同一个键名 Enabled=auto 在真文件里分属 5 个
///    不同 section（[FrameGen] L25、[DLSS] L664、[OutputScaling] L1154、[CAS] L1172、
///    [Magnifier] L1234）。不按 section 限定，一条 edit 就会一次改掉五个毫不相干的功能开关。
/// 3. **只替换 = 右侧**：左半部分（键名连同用户原有的空格）原样保留，这是最小干预的直接体现。
/// </remarks>
public sealed class IniPatcher : IIniPatcher
{
    private const string AddedByDeployer = "; added by OptiScaler Deployer";

    public IniPatchResult Patch(string originalContent, IReadOnlyList<IniEdit> edits)
    {
        // 空 Section / 空 Key 是**硬错误**，不是「匹配不上就算了」：Section 为空会命中
        // 第一个 section 头之前的前言区，Key 为空会命中任何以 = 开头的畸形行；两者都
        // 会让「新建 section」分支往用户的 OptiScaler.ini 末尾追加一个 [] 段——那是往
        // 游戏文件里写垃圾。守卫放在这一层而不是调用方：IniPatcher 是真正落笔的地方，
        // 而 edits 可能来自用户自己写的 profile JSON。
        foreach (var edit in edits)
        {
            if (string.IsNullOrWhiteSpace(edit.Section) || string.IsNullOrWhiteSpace(edit.Key))
            {
                throw new ArgumentException(
                    $"IniEdit 的 Section 与 Key 都不能为空白（实际：Section=\"{edit.Section}\"，Key=\"{edit.Key}\"）。",
                    nameof(edits));
            }
        }

        var newLine = DetectNewLine(originalContent);
        var lines = SplitLines(originalContent);

        if (edits.Count == 0)
        {
            return new IniPatchResult(originalContent, Array.Empty<IniEdit>(), Array.Empty<IniEdit>(), newLine);
        }

        // 后出现的 edit 覆盖先出现的（"last one wins"），但顺序按**首次出现**的位置保留。
        // 不能直接靠 pending.Values 的枚举顺序：Dictionary 一旦 Remove 过，插入序就不再由
        // 规范保证（.NET 8 实测幸存键的相对顺序不变，但那是实现细节、不是契约）。
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

    /// <summary>
    /// 按 \n 切，再去掉每行末尾的 \r。这样混合行尾的文件也能被正确逐行解析。
    /// </summary>
    /// <remarks>
    /// 若按检测到的 newLine 切，夹在 CRLF 文件里的 bare LF 会把相邻两行并成一行，
    /// 那一行里的键会**静默**匹配不上——Join 能原样还原所以文件不会被写坏，
    /// 但配置没生效，这是最难排查的一类失败。
    /// 代价是输出会把零星 bare LF 归一化成主导行尾，这算是对「最小干预」的一次
    /// 有意识让步：换取的是「不会静默漏改」。
    /// 另一个已知的既有行为：输入不以换行结尾时，输出会补一个换行。真 OptiScaler.ini
    /// 以换行结尾，且 edits 为空时 Patch 直接原样返回，所以实际碰不到。
    /// </remarks>
    private static List<string> SplitLines(string content)
    {
        var parts = content.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
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
