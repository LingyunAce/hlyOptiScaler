namespace Deployer.Core.Domain;

/// <summary>
/// 一组 INI 覆盖 + 元数据。Profile 不含游戏特定项
/// （TargetProcessName / LogToFile 由 planner 追加，不属于 Profile）。
/// </summary>
public sealed record UpscalerProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required bool IsBuiltin { get; init; }
    public required IReadOnlyList<IniEdit> Edits { get; init; }
}
