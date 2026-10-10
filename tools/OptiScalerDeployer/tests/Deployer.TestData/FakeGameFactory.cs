using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;

namespace Deployer.TestData;

/// <summary>Creates representative UE, GDK, and conflicting game layouts for in-memory tests.</summary>
public static class FakeGameFactory
{
    public static FakeGame CreateUeGame(MockFileSystem fs, string root = @"C:\Games\FakeUeGame")
    {
        fs.AddDirectory($@"{root}\Engine\Binaries\ThirdParty");
        fs.AddDirectory($@"{root}\SwGame\Content");
        var exe = $@"{root}\SwGame\Binaries\Win64\SwGame-Win64-Shipping.exe";
        fs.AddFile(exe, new MockFileData("UE GAME EXE"));
        fs.AddFile($@"{root}\SwGame\Binaries\Win64\SwGame.exe", new MockFileData("LAUNCHER"));
        return new FakeGame("Fake UE Game", root, $@"{root}\SwGame\Binaries\Win64", exe,
            "SwGame-Win64-Shipping.exe", true);
    }

    public static FakeGame CreateGdkGame(MockFileSystem fs, string root = @"C:\XboxGames\FakeGdkGame\content")
    {
        fs.AddDirectory($@"{root}\Engine\Binaries");
        var exe = $@"{root}\GameMain\Binaries\Win64\GameMain-WinGDK-Shipping.exe";
        fs.AddFile(exe, new MockFileData("GDK GAME EXE"));
        fs.AddFile($@"{root}\GameMain\Binaries\Win64\MicrosoftGame.config", new MockFileData("<Config/>"));
        fs.AddFile($@"{root}\GameMain\Content\Paks\pak0.pak", new MockFileData("PAK"));
        return new FakeGame("Fake GDK Game", root, $@"{root}\GameMain\Binaries\Win64", exe,
            "GameMain-WinGDK-Shipping.exe", true);
    }

    public static FakeGame CreateConflictGame(MockFileSystem fs, string root = @"C:\Games\FakeConflictGame")
    {
        var exe = $@"{root}\Game.exe";
        fs.AddFile(exe, new MockFileData("CONFLICT GAME EXE"));
        fs.AddFile($@"{root}\version.dll", new MockFileData("GAME OWN version.dll"));
        fs.AddFile($@"{root}\winmm.dll", new MockFileData("GAME OWN winmm.dll"));
        fs.AddFile($@"{root}\d3d12.dll", new MockFileData("GAME OWN d3d12.dll"));
        return new FakeGame("Fake Conflict Game", root, root, exe, "Game.exe", false);
    }
}

public sealed record FakeGame(
    string Title,
    string InstallRoot,
    string DeployDirectory,
    string ExecutablePath,
    string ExecutableName,
    bool HasEngineFolder)
{
    public InspectionSummary DefaultInspection { get; } = new()
    {
        GraphicsApis = new[] { GraphicsApi.Dx12 },
        DetectedSdks = new[] { UpscalerSdk.Dlss },
        HasEngineFolder = HasEngineFolder,
        IsVulkanOnly = false,
    };

    public InspectionSummary NoSdkInspection { get; } = new()
    {
        GraphicsApis = new[] { GraphicsApi.Dx12 },
        DetectedSdks = Array.Empty<UpscalerSdk>(),
        HasEngineFolder = HasEngineFolder,
        IsVulkanOnly = false,
    };
}
