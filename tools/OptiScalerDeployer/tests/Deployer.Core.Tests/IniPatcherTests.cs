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
    public void Output_line_count_is_never_less_than_input()
    {
        var patcher = new IniPatcher();
        var inputLines = Sample.Split("\r\n").Length;

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("Log", "LogToFile", "true"),
            Edit("Spoofing", "Dxgi", "false"),   // section 不存在
            Edit("Upscalers", "BrandNewKey", "1"), // key 不存在
        });

        Assert.True(result.Content.Split(result.DetectedNewLine).Length >= inputLines);
    }

    [Fact]
    public void All_comment_lines_survive_verbatim()
    {
        var patcher = new IniPatcher();
        var originalComments = Sample.Split("\r\n").Where(l => l.StartsWith(';')).ToArray();

        var result = patcher.Patch(Sample, new[]
        {
            Edit("Upscalers", "Dx12Upscaler", "ffx"),
            Edit("FrameGen", "Enabled", "true"),
            Edit("Log", "LogLevel", "2"),
        });

        var outputLines = result.Content.Split(result.DetectedNewLine);
        foreach (var comment in originalComments)
        {
            Assert.Contains(comment, outputLines);
        }
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

        var result = patcher.Patch(Sample, new[] { Edit("Log", "SingleFile", "true") });

        var logIndex = result.Content.IndexOf("[Log]", StringComparison.Ordinal);
        var appendedIndex = result.Content.IndexOf("SingleFile=true", StringComparison.Ordinal);
        Assert.True(appendedIndex > logIndex);
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
}
