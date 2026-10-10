namespace Deployer.Core.Inspection;

public static class EngineDetector
{
    public static bool HasEngineDirectory(string installRoot) => Directory.Exists(Path.Combine(installRoot, "Engine"));
}
