namespace Deployer.Core.Domain;

/// <summary>Detailed, safe-to-consume result of inspecting one explicitly selected game executable.</summary>
public sealed record InspectionReport
{
    public required InspectionSummary Summary { get; init; }
    public required DirectoryWritability Writability { get; init; }
    public required IReadOnlyList<PlanWarning> Warnings { get; init; }
    public required IReadOnlyList<IniEdit> RecommendedInputEdits { get; init; }
    public required bool IsSupported { get; init; }
}
