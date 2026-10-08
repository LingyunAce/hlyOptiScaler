using Deployer.Core.Domain;

namespace Deployer.Core.Profiles;

/// <summary>
/// 内置后端策略。核心原则是最小干预（spec §8.2）：只钉死必须确定的键，
/// 其余一律留给 OptiScaler 的 auto 与 per-game quirks。
/// </summary>
public static class BuiltinProfiles
{
    public const string AmdRdna4Id = "amd-rdna4";
    public const string AmdRdna4NativeId = "amd-rdna4-native";
    public const string NvidiaRtxId = "nvidia-rtx";

    /// <summary>RX 9070 XT 主力档。DX11/Vulkan 经 DX12 interop 以吃到 FSR4，代价约 10% 性能。</summary>
    public static UpscalerProfile AmdRdna4 { get; } = new()
    {
        Id = AmdRdna4Id,
        DisplayName = "AMD RDNA4（FSR4，画质优先）",
        Description = "RX 9070 XT 等 RDNA4 显卡。DX11/Vulkan 游戏走 DX12 interop 以启用 FSR4，约有 10% 性能损失。",
        IsBuiltin = true,
        Edits = new[]
        {
            new IniEdit("Upscalers", "Dx12Upscaler", null, "ffx"),
            new IniEdit("Upscalers", "Dx11Upscaler", null, "ffx_12"),
            new IniEdit("Upscalers", "VulkanUpscaler", null, "ffx_12"),
            new IniEdit("FSR", "UpscalerIndex", null, "0"),
            new IniEdit("FSR", "FGIndex", null, "0"),
            new IniEdit("FSR", "Fsr4ForceModel", null, "0"),
            new IniEdit("Spoofing", "Dxgi", null, "false"),
            new IniEdit("Spoofing", "Vulkan", null, "false"),
            new IniEdit("FrameGen", "Enabled", null, "false"),
            new IniEdit("FrameGen", "FGOutput", null, "fsrfg"),
        },
    };

    /// <summary>RDNA4 性能档。DX11/Vulkan 用原生后端，无 interop 损失，但画质上限是 FSR3.1。</summary>
    public static UpscalerProfile AmdRdna4Native { get; } = new()
    {
        Id = AmdRdna4NativeId,
        DisplayName = "AMD RDNA4（原生，性能优先）",
        Description = "DX11 用 FSR3.1、Vulkan 用 FSR2.2 原生后端，无 interop 性能损失；DX12 仍是 FSR4。",
        IsBuiltin = true,
        Edits = new[]
        {
            new IniEdit("Upscalers", "Dx12Upscaler", null, "ffx"),
            new IniEdit("Upscalers", "Dx11Upscaler", null, "fsr31"),
            new IniEdit("Upscalers", "VulkanUpscaler", null, "fsr22"),
            new IniEdit("FSR", "UpscalerIndex", null, "0"),
            new IniEdit("FSR", "FGIndex", null, "0"),
            new IniEdit("FSR", "Fsr4ForceModel", null, "0"),
            new IniEdit("Spoofing", "Dxgi", null, "false"),
            new IniEdit("Spoofing", "Vulkan", null, "false"),
            new IniEdit("FrameGen", "Enabled", null, "false"),
            new IniEdit("FrameGen", "FGOutput", null, "fsrfg"),
        },
    };

    /// <summary>开发机（NVIDIA RTX）自测档。</summary>
    public static UpscalerProfile NvidiaRtx { get; } = new()
    {
        Id = NvidiaRtxId,
        DisplayName = "NVIDIA RTX（DLSS）",
        Description = "输出走 DLSS。帧生成需要 OptiScaler\\streamline\\ 与 nvngx_dlssg.dll，二者不随仓库分发。",
        IsBuiltin = true,
        Edits = new[]
        {
            new IniEdit("Upscalers", "Dx12Upscaler", null, "dlss"),
            new IniEdit("Upscalers", "Dx11Upscaler", null, "dlss"),
            new IniEdit("Upscalers", "VulkanUpscaler", null, "dlss"),
            new IniEdit("Spoofing", "Dxgi", null, "false"),
            new IniEdit("Spoofing", "Vulkan", null, "false"),
            new IniEdit("FrameGen", "Enabled", null, "false"),
            new IniEdit("FrameGen", "FGOutput", null, "dlssg"),
        },
    };

    public static IReadOnlyList<UpscalerProfile> All { get; } = new[] { AmdRdna4, AmdRdna4Native, NvidiaRtx };

    /// <summary>
    /// 帧生成附加档。用户勾选后才叠加到基础 Profile 之上，一律不默认开启。
    /// HUDFix 只在 FGInput=upscaler 时开：那条路径下它是必需的（防 UI 鬼影），
    /// 而 dlssg 输入原生支持 Hudless，开它只增加崩溃风险。依据见方法体注释。
    /// </summary>
    public static IReadOnlyList<IniEdit> FrameGenAddOn(bool useStreamlineInput)
    {
        var edits = new List<IniEdit>
        {
            new("FrameGen", "Enabled", null, "true"),
            new("FrameGen", "FGInput", null, useStreamlineInput ? "dlssg" : "upscaler"),
            new("FrameGen", "FGOutput", null, "fsrfg"),
            new("FSR", "FGIndex", null, "0"),
        };

        // HUDFix 只在 FGInput=upscaler 时才是必需的，而它自带崩溃风险，所以按输入分支决定加不加。
        // 依据是真 OptiScaler.ini 的上游注释（实测行号）：L31 说 upscaler 输入
        // "To prevent UI glitching, Hudfix is required"；L28 说 dlssg 输入
        // "Supports Hudless out of the box"（不需要它）；L317 警告 HUDFix
        // "Might cause crashes, especially with Async"。
        // 在不需要它的路径上开它 = 纯增崩溃风险、零收益，与本项目「部署后健康检查回滚」的
        // 安全模型直接冲突（触发的是回滚，用户看到的是「装了又自己卸了」）。
        if (!useStreamlineInput)
        {
            edits.Add(new IniEdit("OptiFG", "HUDFix", null, "true"));
        }

        return edits;
    }
}
