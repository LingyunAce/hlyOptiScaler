using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using Xunit;

namespace Deployer.Core.Tests;

public class BuiltinProfilesTests
{
    private static string? Value(UpscalerProfile profile, string section, string key) =>
        profile.Edits.FirstOrDefault(e =>
            string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))?.NewValue;

    [Fact]
    public void AmdRdna4_pins_fsr4_indices_instead_of_auto()
    {
        var profile = BuiltinProfiles.AmdRdna4;

        Assert.Equal("ffx", Value(profile, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("ffx_12", Value(profile, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("ffx_12", Value(profile, "Upscalers", "VulkanUpscaler"));
        Assert.Equal("0", Value(profile, "FSR", "UpscalerIndex"));
        Assert.Equal("0", Value(profile, "FSR", "FGIndex"));
        Assert.Equal("0", Value(profile, "FSR", "Fsr4ForceModel"));
    }

    [Fact]
    public void AmdRdna4_disables_spoofing_because_amd_needs_no_nvidia_mask()
    {
        var profile = BuiltinProfiles.AmdRdna4;

        Assert.Equal("false", Value(profile, "Spoofing", "Dxgi"));
        Assert.Equal("false", Value(profile, "Spoofing", "Vulkan"));
    }

    [Fact]
    public void No_builtin_profile_enables_framegen_by_default()
    {
        foreach (var profile in BuiltinProfiles.All)
        {
            var enabled = Value(profile, "FrameGen", "Enabled");
            Assert.True(enabled is null or "false",
                $"{profile.Id} 默认开启了帧生成（实际值 {enabled}）");
        }
    }

    [Fact]
    public void AmdRdna4Native_avoids_interop_and_therefore_caps_at_fsr31()
    {
        var profile = BuiltinProfiles.AmdRdna4Native;

        Assert.Equal("ffx", Value(profile, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("fsr31", Value(profile, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("fsr22", Value(profile, "Upscalers", "VulkanUpscaler"));
    }

    [Fact]
    public void NvidiaRtx_uses_dlss_output()
    {
        var profile = BuiltinProfiles.NvidiaRtx;

        Assert.Equal("dlss", Value(profile, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("dlss", Value(profile, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("dlssg", Value(profile, "FrameGen", "FGOutput"));
    }

    [Fact]
    public void FrameGenAddOn_sets_hudfix_and_upscaler_input()
    {
        var edits = BuiltinProfiles.FrameGenAddOn(useStreamlineInput: false);

        var lookup = edits.ToDictionary(e => (e.Section, e.Key));
        Assert.Equal("true", lookup[("FrameGen", "Enabled")].NewValue);
        Assert.Equal("upscaler", lookup[("FrameGen", "FGInput")].NewValue);
        Assert.Equal("fsrfg", lookup[("FrameGen", "FGOutput")].NewValue);
        Assert.Equal("0", lookup[("FSR", "FGIndex")].NewValue);
        Assert.Equal("true", lookup[("OptiFG", "HUDFix")].NewValue);
    }

    [Fact]
    public void FrameGenAddOn_uses_dlssg_input_when_game_has_streamline()
    {
        var edits = BuiltinProfiles.FrameGenAddOn(useStreamlineInput: true);

        Assert.Equal("dlssg",
            edits.Single(e => e.Section == "FrameGen" && e.Key == "FGInput").NewValue);
        // dlssg 输入原生支持 Hudless（真 ini L28），不需要 HUDFix；而 HUDFix 自带崩溃风险
        // （L317 "Might cause crashes, especially with Async"），所以这条路径上不应出现它。
        Assert.DoesNotContain(edits, e => e.Section == "OptiFG" && e.Key == "HUDFix");
    }

    [Fact]
    public void All_builtin_ids_are_unique()
    {
        var ids = BuiltinProfiles.All.Select(p => p.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_builtins_are_marked_builtin()
    {
        Assert.All(BuiltinProfiles.All, p => Assert.True(p.IsBuiltin));
    }

    [Fact]
    public void All_ships_exactly_the_three_profiles_in_order()
    {
        // 钉死 Id 与顺序：UI 的下拉框、以及「9070 XT 默认选哪个」都依赖它。
        // 期望值必须是字面量——写成 BuiltinProfiles.All.Select(...) 就等于没测：
        // 复核时从 All 里摘掉 NvidiaRtx，全套 73 个测试依然全绿。
        Assert.Equal(
            new[] { "amd-rdna4", "amd-rdna4-native", "nvidia-rtx" },
            BuiltinProfiles.All.Select(p => p.Id).ToArray());
    }
}
