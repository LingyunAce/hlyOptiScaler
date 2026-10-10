using Xunit;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Inspection;
using Deployer.TestData;

namespace Deployer.Core.Tests;

public sealed class GameInspectorTests
{
    [Fact]
    public async Task InspectsSelectedX64AndRecommendsActualInputKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllBytes(Path.Combine(root, "bin", "game.exe"), PeFixtureBuilder.BuildAmd64(["d3d11.dll", "vulkan-1.dll", "libxess.dll"]));
            File.WriteAllBytes(Path.Combine(root, "nvngx_dlss.dll"), [1]);
            var report = await new GameInspector(new FileSystem(), _ => DirectoryWritability.Writable)
                .InspectAsync(Target(root, "bin", "game.exe"));
            Assert.True(report.IsSupported);
            Assert.Equal(new[] { GraphicsApi.Dx11, GraphicsApi.Vulkan }, report.Summary.GraphicsApis);
            Assert.Equal(new[] { UpscalerSdk.Dlss, UpscalerSdk.XeSS }, report.Summary.DetectedSdks);
            Assert.Contains(report.RecommendedInputEdits, e => e.Key == "EnableXeSSInputs" && e.NewValue == "true");
            Assert.Contains(report.RecommendedInputEdits, e => e.Key == "EnableDlssInputs" && e.NewValue == "true");
            Assert.Contains(report.RecommendedInputEdits, e => e.Key == "EnableFsr2Inputs" && e.NewValue == "false");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RejectsX86AndManagedExecutablesAndPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "x86.exe"), PeFixtureBuilder.BuildX86(["d3d11.dll"]));
            File.WriteAllBytes(Path.Combine(root, "pe32.exe"), PeFixtureBuilder.BuildAmd64Pe32(["nvngx_dlss.dll"]));
            var inspector = new GameInspector(new FileSystem(), _ => DirectoryWritability.Writable);
            var report = await inspector.InspectAsync(Target(root, "", "x86.exe"));
            Assert.False(report.IsSupported); Assert.Contains(report.Warnings, w => w.Severity == WarningSeverity.Blocking);
            report = await inspector.InspectAsync(Target(root, "", "pe32.exe")); Assert.False(report.IsSupported);
            File.WriteAllBytes(Path.Combine(root, "managed.exe"), PeFixtureBuilder.BuildManagedAmd64(["nvngx_dlss.dll"]));
            report = await inspector.InspectAsync(Target(root, "", "managed.exe")); Assert.False(report.IsSupported);
            report = await inspector.InspectAsync(Target(root, "", "..\\outside.exe")); Assert.False(report.IsSupported);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RejectsDeployDirectoryOutsideGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "inspect-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "game.exe"), PeFixtureBuilder.BuildAmd64(["nvngx_dlss.dll"]));
            var report = await new GameInspector(new FileSystem()).InspectAsync(Target(root, "", "game.exe") with { DeployDirectory = outside });
            Assert.False(report.IsSupported);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task CancellationIsObservedAndEngineRootIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Engine"));
            File.WriteAllBytes(Path.Combine(root, "game.exe"), PeFixtureBuilder.BuildAmd64(["nvngx_dlss.dll"]));
            var inspector = new GameInspector(new FileSystem(), _ => DirectoryWritability.Writable);
            var report = await inspector.InspectAsync(Target(root, "", "game.exe"));
            Assert.True(report.Summary.HasEngineFolder);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync(Target(root, "", "game.exe"), cts.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StreamlineFrameGenRequiresUpscalerSdkAndNativeFgEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "game.exe"), PeFixtureBuilder.BuildAmd64(["sl.dlss.dll", "sl.interposer.dll"]));
            File.WriteAllBytes(Path.Combine(root, "sl.dlss_g.dll"), [1]);
            var report = await new GameInspector(new FileSystem(), _ => DirectoryWritability.Writable).InspectAsync(Target(root, "", "game.exe"));
            Assert.Contains(UpscalerSdk.Streamline, report.Summary.DetectedSdks);
            Assert.True(report.Summary.HasStreamlineFrameGen);
            Assert.Contains(report.RecommendedInputEdits, e => e.Key == "EnableDlssInputs" && e.NewValue == "true");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MockFileSystemUsesInjectedVersionReaderForSdkProxyExclusion()
    {
        const string root = @"C:\Games\MockGame";
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(root);
        fs.File.WriteAllBytes(Path.Combine(root, "game.exe"), PeFixtureBuilder.BuildAmd64(["d3d11.dll"]));
        fs.File.WriteAllBytes(Path.Combine(root, "nvngx_dlss.dll"), [1]);
        var inspector = new GameInspector(fs, _ => DirectoryWritability.Writable, new TestVersionReader(null));
        var report = await inspector.InspectAsync(Target(root, "", "game.exe"));
        Assert.Contains(UpscalerSdk.Dlss, report.Summary.DetectedSdks);

        report = await new GameInspector(fs, _ => DirectoryWritability.Writable, new TestVersionReader("OptiScaler.dll"))
            .InspectAsync(Target(root, "", "game.exe"));
        Assert.DoesNotContain(UpscalerSdk.Dlss, report.Summary.DetectedSdks);
        Assert.Contains(report.Warnings, warning => warning.Severity == WarningSeverity.Blocking);
    }

    [Fact]
    public async Task WindowsAppsPhysicalInstallIsBlockedEvenWhenPathContainsXboxGames()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N"));
        var physicalRoot = Path.Combine(baseDir, "WindowsApps", "Package");
        var mixedRoot = Path.Combine(baseDir, "WindowsApps", "XboxGames", "Title");
        var xboxRoot = Path.Combine(baseDir, "XboxGames", "Title");
        Directory.CreateDirectory(physicalRoot); Directory.CreateDirectory(mixedRoot); Directory.CreateDirectory(xboxRoot);
        try
        {
            var blocked = await new GameInspector(new FileSystem()).InspectAsync(Target(physicalRoot, "", "missing.exe"));
            Assert.False(blocked.IsSupported);
            Assert.Contains(blocked.Warnings, warning => warning.Severity == WarningSeverity.Blocking && warning.Message.Contains("WindowsApps", StringComparison.Ordinal));

            var mixed = await new GameInspector(new FileSystem()).InspectAsync(
                Target(mixedRoot, "", "missing.exe") with { Platform = GamePlatform.Xbox });
            Assert.False(mixed.IsSupported);
            Assert.Contains(mixed.Warnings, warning => warning.Severity == WarningSeverity.Blocking);

            File.WriteAllBytes(Path.Combine(xboxRoot, "game.exe"), PeFixtureBuilder.BuildAmd64(["nvngx_dlss.dll"]));
            var allowed = await new GameInspector(new FileSystem(), _ => DirectoryWritability.Writable)
                .InspectAsync(Target(xboxRoot, "", "game.exe") with { Platform = GamePlatform.Xbox });
            Assert.True(allowed.IsSupported);
        }
        finally { Directory.Delete(baseDir, true); }
    }

    private static GameTarget Target(string root, string deploySubdir, string exe) => new("Test", GamePlatform.Steam, null, null,
        root, Path.Combine(root, deploySubdir), exe, "fingerprint");

    private sealed class TestVersionReader(string? originalFilename) : IFileVersionReader
    {
        public string? GetOriginalFilename(string path) => originalFilename;
        public string? GetFileVersion(string path) => null;
    }
}
