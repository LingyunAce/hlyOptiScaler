namespace Deployer.Core.Domain;

/// <summary>
/// 游戏体检结果中 planner 需要的部分。Plan B 的 IGameInspector 填充该类型；
/// 手工 PlanRequest 若不填可空帧生成字段，会沿用旧的 SDK 推断行为。
/// </summary>
public sealed record InspectionSummary
{
    public required IReadOnlyList<GraphicsApi> GraphicsApis { get; init; }
    public required IReadOnlyList<UpscalerSdk> DetectedSdks { get; init; }
    public required bool HasEngineFolder { get; init; }
    public required bool IsVulkanOnly { get; init; }
    /// <summary>Whether Streamline DLSS-G frame generation was detected; null preserves older/manual request inference.</summary>
    public bool? HasStreamlineFrameGen { get; init; }

    /// <summary>
    /// 游戏没有任何超分 SDK —— 装了 OptiScaler 也不会生效。
    /// planner 据此产出 Blocking 级警告。
    /// </summary>
    public bool HasAnyUpscalerSdk => DetectedSdks.Count > 0;

    public static InspectionSummary Empty { get; } = new()
    {
        GraphicsApis = Array.Empty<GraphicsApi>(),
        DetectedSdks = Array.Empty<UpscalerSdk>(),
        HasEngineFolder = false,
        IsVulkanOnly = false,
    };
}
