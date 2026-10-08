using Deployer.Core.Domain;
using Deployer.Core.Ini;
using Xunit;

namespace Deployer.Core.Tests;

public class IniPatcherTests
{
    private const string Sample =
        "; OptiScaler config\r\n" +
        "[Upscalers]\r\n" +
        "; fsr21, fsr22, ffx, dlss\r\n" +
        "Dx12Upscaler=auto\r\n" +
        "\r\n" +
        "[FrameGen]\r\n" +
        "Enabled=auto\r\n" +
        "\r\n" +
        "[Log]\r\n" +
        "LogToFile=auto\r\n" +
        "LogLevel=auto\r\n";

    private static IniEdit Edit(string section, string key, string value) =>
        new(section, key, null, value);

    /// <summary>以 ; 或 # 开头的行（允许前导空白）。</summary>
    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(';') || trimmed.StartsWith('#');
    }

    /// <summary>断言 <paramref name="input"/> 的每一行都按原顺序出现在 <paramref name="output"/> 里。</summary>
    private static void AssertIsSupersequence(string[] input, string[] output)
    {
        var i = 0;
        foreach (var line in output)
        {
            if (i < input.Length && input[i] == line)
            {
                i++;
            }
        }

        Assert.Equal(input.Length, i);
    }

    [Fact]
    public void Replaces_value_in_place_and_preserves_comment_lines()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Contains("Dx12Upscaler=ffx", result.Content);
        Assert.Contains("; fsr21, fsr22, ffx, dlss", result.Content);
        Assert.Contains("; OptiScaler config", result.Content);
        Assert.DoesNotContain("Dx12Upscaler=auto", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Equal("auto", result.AppliedEdits[0].OldValue);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Output_preserves_every_input_line_in_order()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("Log", "LogToFile", "true"),
            Edit("Spoofing", "Dxgi", "false"),   // section 不存在
            Edit("Upscalers", "BrandNewKey", "1"), // key 不存在
        });

        var outputLines = result.Content.Split(result.DetectedNewLine);

        // 只断言「输出行数 >= 输入行数」是恒真式：实现从不删行，那个断言唯一能捕获的退化
        // 是「有人开始删行」，而它捕不到「行放错了位置」（行数完全正确）。超序列断言同时
        // 覆盖删行、改行、重排三类退化——输入的每一行都必须按原顺序出现在输出里。
        // 注意必须在 split 之后的行数组上比，比原文会被行尾归一化误报。
        //
        // 上面两条就地命中的 edit 是**契约允许**的改行（「只替换 = 右侧，左半部分原样保留」），
        // 所以超序列要比的是「已应用就地替换后的输入行」。直接拿原始 Sample 的行去比是恒假的：
        // Dx12Upscaler=auto 本来就不应该出现在输出里。这里把预期改动显式写成映射，
        // 于是断言仍然要求：左侧原样保留、其余每一行逐字原样且顺序不变。
        var survivingInputLines = Sample
            .Split("\r\n")
            .Select(l => l switch
            {
                "Dx12Upscaler=auto" => "Dx12Upscaler=ffx",
                "LogToFile=auto" => "LogToFile=true",
                _ => l,
            })
            .ToArray();

        AssertIsSupersequence(survivingInputLines, outputLines);
    }

    [Fact]
    public void All_comment_lines_survive_verbatim()
    {
        var patcher = new IniPatcher();
        var originalComments = Sample.Split("\r\n").Where(IsComment).ToArray();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("FrameGen", "Enabled", "true"),
            Edit("Log", "LogLevel", "2"),
        });

        var outputComments = result.Content
            .Split(result.DetectedNewLine)
            .Where(l => IsComment(l) && l != "; added by OptiScaler Deployer")
            .ToArray();

        // 有序 + 带重数的比对。旧写法 Assert.Contains(comment, outputLines) 是**集合包含**：
        // 真 OptiScaler.ini 里 `; ----` 横幅重复了几十次（L1/L3/L19/L21 完全相同），
        // 删掉其中三条只要还剩一条相同的就照样绿。排掉 AddedByDeployer 是因为它是新增的注释。
        Assert.Equal(originalComments, outputComments);
    }

    [Fact]
    public void Same_key_name_in_different_sections_is_scoped_correctly()
    {
        const string ini =
            "[FrameGen]\r\nEnabled=auto\r\n\r\n[OptiFG]\r\nEnabled=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("FrameGen", "Enabled", "true") });

        Assert.Equal("[FrameGen]\r\nEnabled=true\r\n\r\n[OptiFG]\r\nEnabled=auto\r\n", result.Content);
    }

    [Fact]
    public void Missing_key_is_appended_to_end_of_matching_section()
    {
        var patcher = new IniPatcher();

        // OldValue 故意传一个非 null 的脏值：追加项的 OldValue 必须由 patcher 清成 null，
        // 否则拿上一轮 patch 过的 edits 再 patch 一次时，UI 会渲染出一个根本不存在的
        // 「旧值 → 新值」diff。用 Edit() helper（它传 null）测不出这件事——那测的是 helper。
        var stale = new IniEdit("Log", "SingleFile", "stale-from-a-previous-run", "true");

        var result = patcher.Patch(Sample, new[] { stale });

        // 精确全文断言：[Log] 是 Sample 的最后一个 section，追加必须落在 LogLevel 之后、
        // 也就是文件末尾。只断言「在 [Log] 之后」区分不出「落在段末」与「落在下一个 section 里」。
        Assert.Equal(Sample + "SingleFile=true\r\n", result.Content);
        Assert.Single(result.AppendedEdits);
        Assert.Null(result.AppendedEdits[0].OldValue);
    }

    [Fact]
    public void Missing_section_is_created_at_end_of_file_with_source_comment()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[] { Edit("Spoofing", "Dxgi", "false") });

        Assert.EndsWith("[Spoofing]\r\nDxgi=false\r\n", result.Content);
        Assert.Contains("; added by OptiScaler Deployer", result.Content);
        Assert.Single(result.AppendedEdits);
    }

    [Fact]
    public void Several_keys_appended_to_one_section_keep_their_original_order()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Zzz", "3"),
            Edit("Upscalers", "Aaa", "1"),
            Edit("Upscalers", "Mmm", "2"),
        });

        var body = result.Content.Split(result.DetectedNewLine);
        var iAaa = Array.IndexOf(body, "Aaa=1");
        var iMmm = Array.IndexOf(body, "Mmm=2");
        var iZzz = Array.IndexOf(body, "Zzz=3");

        Assert.True(iZzz >= 0 && iZzz < iAaa && iAaa < iMmm,
            $"追加顺序应为 Zzz、Aaa、Mmm（实际下标 {iZzz}, {iAaa}, {iMmm}）");
    }

    [Fact]
    public void Appended_key_lands_inside_its_section_not_after_the_blank_separator()
    {
        const string ini = "[Upscalers]\r\nDx12Upscaler=auto\r\n\r\n[Log]\r\nLogToFile=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "NewKey", "1") });

        Assert.Equal("[Upscalers]\r\nDx12Upscaler=auto\r\nNewKey=1\r\n\r\n[Log]\r\nLogToFile=auto\r\n",
            result.Content);
    }

    [Fact]
    public void Appends_into_two_different_existing_sections_both_land_at_their_own_section_end()
    {
        // 这是 Critical 缺陷的回归看守：插入使**后半部分所有 section** 的锚点过期，
        // 只更新当前 section 会让后一个 section 的新键落到它的 section 头之前，
        // 也就是写进上一个 section。既有的追加测试全都只碰一个 section，盖不到。
        const string ini = "[A]\r\nA1=1\r\n\r\n[B]\r\nB1=1\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[]
        {
            Edit("A", "X1", "1"),
            Edit("A", "X2", "2"),
            Edit("B", "Y", "9"),
        });

        Assert.Equal("[A]\r\nA1=1\r\nX1=1\r\nX2=2\r\n\r\n[B]\r\nB1=1\r\nY=9\r\n", result.Content);
    }

    [Fact]
    public void Matching_is_case_insensitive_for_section_and_key_but_preserves_original_spelling()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[] { Edit("upscalers", "dx12upscaler", "ffx") });

        Assert.Contains("Dx12Upscaler=ffx", result.Content);
        Assert.DoesNotContain("dx12upscaler=", result.Content);
    }

    [Fact]
    public void Key_with_spaces_around_equals_is_matched_and_left_side_preserved()
    {
        const string ini = "[Upscalers]\r\nDx12Upscaler = auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Equal("[Upscalers]\r\nDx12Upscaler =ffx\r\n", result.Content);
    }

    [Fact]
    public void Lf_only_file_is_detected_and_preserved()
    {
        const string ini = "[Upscalers]\nDx12Upscaler=auto\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Equal("\n", result.DetectedNewLine);
        Assert.Equal("[Upscalers]\nDx12Upscaler=ffx\n", result.Content);
    }

    [Fact]
    public void Empty_edit_list_returns_content_unchanged()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, Array.Empty<IniEdit>());

        Assert.Equal(Sample, result.Content);
        Assert.Empty(result.AppliedEdits);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Duplicate_edits_for_same_key_last_one_wins()
    {
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("Upscalers", "Dx12Upscaler", "dlss"),
        });

        Assert.Contains("Dx12Upscaler=dlss", result.Content);
        Assert.Single(result.AppliedEdits);
    }

    [Fact]
    public void Blank_section_is_rejected_instead_of_appending_an_empty_section_header()
    {
        var patcher = new IniPatcher();

        // 不拦的话，Section 为空的 edit 会命中前言区或走进「新建 section」分支，
        // 往用户的 OptiScaler.ini 末尾追加一个 [] 段——那是往游戏文件里写垃圾。
        var ex = Assert.Throws<ArgumentException>(() => patcher.Patch(Sample, new[] { Edit("", "Foo", "1") }));

        // 钉住消息里含 Section，这样消息退化成裸堆栈时会红
        Assert.Contains("Section", ex.Message);
    }

    [Fact]
    public void Blank_key_is_rejected()
    {
        var patcher = new IniPatcher();

        // 用空白而非空串，确保守卫用的是 IsNullOrWhiteSpace 而不是 IsNullOrEmpty；
        // Key 为空会命中任何以 = 开头的畸形行。
        var ex = Assert.Throws<ArgumentException>(() => patcher.Patch(Sample, new[] { Edit("Upscalers", "   ", "1") }));

        Assert.Contains("Key", ex.Message);
    }

    [Fact]
    public void Section_and_key_with_surrounding_whitespace_are_trimmed_instead_of_creating_a_duplicate_section()
    {
        // " Log " 匹配不上文件里的 "Log"，旧行为会往文件末尾追加一个字面的 [ Log ] 段，
        // 而 INI 解析器读它时 trim 内侧空格得到 Log —— 游戏文件里于是出现两个同名 section。
        const string ini = "[Log]\r\nLogToFile=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit(" Log ", " LogToFile ", "true") });

        Assert.Equal("[Log]\r\nLogToFile=true\r\n", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Empty(result.AppendedEdits);
    }

    [Fact]
    public void Mixed_line_endings_still_get_patched_instead_of_silently_skipping()
    {
        // 第 2 行以 bare LF 结尾。改之前这条 edit 会静默不生效：
        // Foo=1\nDx12Upscaler=auto 被当成一行，匹配到的是 Foo。输出把那个 bare LF
        // 归一化成 CRLF 是有意的（对「最小干预」的有意识让步，换取「不会静默漏改」）。
        const string ini = "[Upscalers]\r\nFoo=1\nDx12Upscaler=auto\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("Upscalers", "Dx12Upscaler", "ffx") });

        Assert.Single(result.AppliedEdits);
        Assert.Equal("auto", result.AppliedEdits[0].OldValue);
        Assert.Equal("[Upscalers]\r\nFoo=1\r\nDx12Upscaler=ffx\r\n", result.Content);
    }

    [Fact]
    public void Appended_keys_keep_edit_order_even_when_other_edits_matched_in_place()
    {
        // 钉住的是**可观察契约**：追加顺序 = 这些 edit 在入参里首次出现的顺序，
        // 与「同一批里有多少条是就地命中」无关。就地命中会触发 pending.Remove，
        // 而实现刻意不依赖 Dictionary 删除后的枚举序（理由写在 IniPatcher.cs 里
        // pending / orderedKeys 上方的注释）。
        // 既有的 Several_keys_appended… 是纯追加场景，从不 Remove，盖不到这条。
        var patcher = new IniPatcher();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Log", "Zzz", "3"),
            Edit("Upscalers", "Dx12Upscaler", "ffx"),  // 就地命中，触发 Remove
            Edit("Log", "Aaa", "1"),
            Edit("Log", "LogLevel", "2"),              // 又一处就地命中
            Edit("Log", "Mmm", "2"),
        });

        Assert.Equal(2, result.AppliedEdits.Count);
        Assert.Equal(3, result.AppendedEdits.Count);

        var body = result.Content.Split(result.DetectedNewLine);
        var iZzz = Array.IndexOf(body, "Zzz=3");
        var iAaa = Array.IndexOf(body, "Aaa=1");
        var iMmm = Array.IndexOf(body, "Mmm=2");

        Assert.True(iZzz >= 0 && iZzz < iAaa && iAaa < iMmm,
            $"追加顺序应为 Zzz、Aaa、Mmm（实际下标 {iZzz}, {iAaa}, {iMmm}）");
    }

    [Fact]
    public void Section_header_with_trailing_inline_comment_is_still_recognised()
    {
        // `[B] ; note` 是合法 INI 写法。不认它的话 currentSection 会停留在 A，
        // 于是针对 A/K 的 edit 会改到物理上位于 B 段的那个 K——改错 section，静默。
        const string ini = "[A]\r\nX=1\r\n[B] ; note\r\nK=2\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "K", "9") });

        // A 段没有 K，所以应当**追加**到 A 段末尾，B 段的 K=2 一个字符都不能动
        Assert.Equal("[A]\r\nX=1\r\nK=9\r\n[B] ; note\r\nK=2\r\n", result.Content);
        Assert.Single(result.AppendedEdits);
        Assert.Empty(result.AppliedEdits);
    }

    [Fact]
    public void Line_starting_with_bracket_but_without_closing_bracket_is_skipped_entirely()
    {
        // `[Foo=bar` 不是 section 头。旧行为会把它当成键名 `[Foo` 解析，
        // 那是在往游戏文件里制造一个谁也不认识的键。
        // 第二条 edit 的 Key 故意取成 `[Broken`——正好等于那条畸形行 `=` 的左侧：
        // 只有这样才能观察到「整行跳过」与「掉进键解析」的区别。对任何正常 Key
        // 而言两者结果相同（畸形行匹配不上就被静默忽略），所以只靠一条普通 edit 钉不住这个分支。
        const string ini = "[A]\r\nK=1\r\n[Broken=bar\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "K", "9"), Edit("A", "[Broken", "9") });

        // `[Broken=bar` 一个字符都不能动；`[Broken` 在 A 段不是键，所以走追加，落在段末
        Assert.Equal("[A]\r\nK=9\r\n[Broken=bar\r\n[Broken=9\r\n", result.Content);
        Assert.Single(result.AppliedEdits);
        Assert.Single(result.AppendedEdits);
    }

    [Fact]
    public void Hash_comment_lines_are_never_parsed_as_keys()
    {
        // Sample 常量里没有 # 注释行，所以 All_comment_lines_survive_verbatim 单靠它盖不到 # 分支。
        // 这里用一个 Key 字面上等于 # 注释行 `=` 左侧的 edit 来钉住：# 行必须整行当注释跳过，
        // 绝不参与键解析。少了 `|| trimmed.StartsWith('#')` 的话实现会去改写这条注释，
        // 而 IIniPatcher 承诺「所有以 ; 或 # 开头的行原样保留」。
        const string ini = "[A]\r\n#Legacy=1\r\nK=2\r\n";
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "#Legacy", "9") });

        // 注释行一个字符都不能动；#Legacy 在 A 段不是键，所以走追加，落在段末
        Assert.Equal("[A]\r\n#Legacy=1\r\nK=2\r\n#Legacy=9\r\n", result.Content);
        Assert.Empty(result.AppliedEdits);
        Assert.Single(result.AppendedEdits);
    }

    [Fact]
    public void Input_without_trailing_newline_gains_one_when_edits_are_applied()
    {
        // 已知且**有意**的行为：edits 非空时 Join 会给不以换行结尾的输入补一个尾换行。
        // 钉住它是为了防止后来者把这当 bug「修」掉——空 edits 走早返回分支，不补。
        const string ini = "[A]\r\nK=1";       // 注意：不以换行结尾
        var patcher = new IniPatcher();

        var result = patcher.Patch(ini, new[] { Edit("A", "K", "2") });

        Assert.Equal("[A]\r\nK=2\r\n", result.Content);
    }
}
