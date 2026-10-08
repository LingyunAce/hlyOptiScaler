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
        // 用户手写 profile JSON 时很容易在 Section / Key 前后多打空格（" Log "）。不归一化的话
        // 它匹配不上文件里的 "Log"，会掉进「新建 section」分支往文件末尾追加一个字面的
        // `[ Log ]` 段——而 INI 解析器读它时会 trim 内侧空格得到 Log，于是游戏文件里出现两个
        // 同名 section、值互相冲突。这里选择 Trim 而不是拒绝：意图是明确的、错误是可恢复的，
        // 不该让整次部署失败。归一化后的 edits 也会原样出现在返回的 AppliedEdits / AppendedEdits 里。
        var normalizedEdits = new List<IniEdit>(edits.Count);
        foreach (var edit in edits)
        {
            normalizedEdits.Add(edit with
            {
                Section = edit.Section?.Trim() ?? string.Empty,
                Key = edit.Key?.Trim() ?? string.Empty,
            });
        }

        // 空 Section / 空 Key 是**硬错误**，不是「匹配不上就算了」：Section 为空会命中
        // 第一个 section 头之前的前言区，Key 为空会命中任何以 = 开头的畸形行；两者都
        // 会让「新建 section」分支往用户的 OptiScaler.ini 末尾追加一个 [] 段——那是往
        // 游戏文件里写垃圾。守卫放在这一层而不是调用方：IniPatcher 是真正落笔的地方，
        // 而 edits 可能来自用户自己写的 profile JSON。
        foreach (var edit in normalizedEdits)
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

        if (normalizedEdits.Count == 0)
        {
            return new IniPatchResult(originalContent, Array.Empty<IniEdit>(), Array.Empty<IniEdit>(), newLine);
        }

        // 后出现的 edit 覆盖先出现的（"last one wins"），但顺序按**首次出现**的位置保留。
        // 不能直接靠 pending.Values 的枚举顺序：Dictionary 一旦 Remove 过，插入序就不再由
        // 规范保证（.NET 8 实测幸存键的相对顺序不变，但那是实现细节、不是契约）。
        var pending = new Dictionary<(string Section, string Key), IniEdit>(KeyComparer.Instance);
        var orderedKeys = new List<(string Section, string Key)>();
        foreach (var edit in normalizedEdits)
        {
            var key = (edit.Section, edit.Key);
            if (!pending.ContainsKey(key))
            {
                orderedKeys.Add(key);
            }

            pending[key] = edit;
        }

        var applied = new List<IniEdit>();
        var sectionAppendAnchor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;   // 注释行：原样保留，绝不参与解析
            }

            if (trimmed.StartsWith('['))
            {
                // 以 [ 开头的行一律不当作键解析：要么是 section 头，要么整行跳过。
                if (TryReadSectionHeader(trimmed, out var sectionName))
                {
                    if (currentSection.Length > 0)
                    {
                        sectionAppendAnchor[currentSection] = LastContentLineIndex(lines, i - 1);
                    }

                    currentSection = sectionName;
                }

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
            sectionAppendAnchor[currentSection] = LastContentLineIndex(lines, lines.Count - 1);
        }

        var appended = new List<IniEdit>();
        var createdSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in orderedKeys)
        {
            if (!pending.TryGetValue(key, out var edit))
            {
                continue;   // 已在上面就地替换过
            }

            if (sectionAppendAnchor.TryGetValue(edit.Section, out var anchorLine) && anchorLine >= 0)
            {
                lines.Insert(anchorLine + 1, edit.Key + "=" + edit.NewValue);

                // 插入使插入点之后的所有行整体下移一行，因此**每一个**指向后半部分的锚点都过期了。
                // 只更新当前 section 会让后面 section 的锚点偏差，偏差累积到超过该 section 的长度时，
                // 新键会落到 section 头之前——也就是写进上一个 section，直接击穿 section 作用域这条
                // 不变量。section 总数最多几十个，整体扫一遍的代价可以忽略。
                foreach (var pair in sectionAppendAnchor.ToArray())
                {
                    if (pair.Value >= anchorLine + 1)
                    {
                        sectionAppendAnchor[pair.Key] = pair.Value + 1;
                    }
                }

                // 当前 section 的锚点推到刚插入的那一行，同 section 的下一条接着排在它后面
                sectionAppendAnchor[edit.Section] = anchorLine + 1;
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
                sectionAppendAnchor[edit.Section] = lines.Count - 1;
            }

            appended.Add(edit.WithOldValue(null));
        }

        return new IniPatchResult(Join(lines, newLine), applied, appended, newLine);
    }

    /// <summary>
    /// 识别 section 头：以 [ 开头、扫到**第一个** ] 为止，] 之后的内容（行内注释）一律忽略，
    /// 内侧空白 trim 掉。没有 ] 就返回 false，整行由调用方跳过。
    /// </summary>
    /// <remarks>
    /// 为什么 `]` 后面允许有内容：`[Log] ; 只写日志` 是完全合法的 INI 写法，通行解析器都认。
    /// 要求 `]` 必须在行尾会让这一行既不是 section 头、又没有 `=`，于是被当成空行跳过——
    /// currentSection 保持不变，后面所有键被归到**上一个** section 名下。失效方向是
    /// 「改到别的 section 的键」与「往文件末尾追加一个重复 section」，两者都是静默的。
    /// 为什么没有 `]` 时整行跳过而不是当键解析：`[Foo=bar` 会被解析成键名 `[Foo`，
    /// 那是在往游戏文件里制造一个谁也不认识的键。
    /// </remarks>
    private static bool TryReadSectionHeader(string trimmedLine, out string sectionName)
    {
        sectionName = string.Empty;

        var close = trimmedLine.IndexOf(']');
        if (close < 0)
        {
            return false;
        }

        sectionName = trimmedLine[1..close].Trim();
        return true;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 往前找最后一个非空白行，作为该 section 的追加锚点。
    /// </summary>
    /// <remarks>
    /// 注意**注释行算内容**：真 OptiScaler.ini 里很多 section 末尾有 `; ----` 横幅，
    /// 新键会落在横幅**之后**而不是紧贴最后一个真实键。这是有意的——横幅在语义上属于
    /// 它上面那个 section 的收尾，把新键插在横幅与它描述的键之间才是真的拆散了注释。
    /// 全是空白行时返回 -1；调用处那个 `>= 0` 判断在当前所有路径下恒为真（三个写入点
    /// 都保证了非负），留着是防将来有人新增一个可能写入 -1 的路径。
    /// </remarks>
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
