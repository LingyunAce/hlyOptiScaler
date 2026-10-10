using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Deployer.Core.Abstractions;

namespace Deployer.TestData;

/// <summary>Creates a representative synthetic payload using the OptiScaler post-build output layout.</summary>
public static class SyntheticPayloadFactory
{
    public const string OptiScalerDllName = "OptiScaler.dll";
    public const string IniFileName = "OptiScaler.ini";
    public const string DefaultVersion = "0.9.1.0";

    public static IReadOnlyList<string> RelativeFiles { get; } = new[]
    {
        OptiScalerDllName,
        IniFileName,
        @"OptiScaler\amd_fidelityfx_loader_dx12.dll",
        @"OptiScaler\amd_fidelityfx_upscaler_dx12.dll",
        @"OptiScaler\amd_fidelityfx_framegeneration_dx12.dll",
        @"OptiScaler\amd_fidelityfx_vk.dll",
        @"OptiScaler\amdxc64.dll",
        @"OptiScaler\libxess.dll",
        @"OptiScaler\Licenses\FidelityFX_v1_LICENSE.md",
        @"OptiScaler\D3D12_OptiScaler\d3d12core.dll",
        @"Licenses\FidelityFX_v2_LICENSE.md",
        @"Licenses\DirectX_LICENSE.txt",
        @"Licenses\XeSS_LICENSE.txt",
        "setup_windows.bat",
        "setup_linux.sh",
        "!! EXTRACT ALL FILES TO GAME FOLDER !!",
    };

    public static IReadOnlyList<string> SkippedFileNames { get; } = new[]
    {
        "setup_windows.bat", "setup_linux.sh", "!! EXTRACT ALL FILES TO GAME FOLDER !!",
    };

    public static string SampleIniContent { get; } = string.Join("\r\n", new[]
    {
        "; OptiScaler configuration",
        "[Upscalers]",
        "; fsr21, fsr22, ffx (FSR 2.3; 3.1; 4.x), dlss",
        "Dx11Upscaler=auto",
        "Dx12Upscaler=auto",
        "VulkanUpscaler=auto",
        "",
        "[FrameGen]",
        "; nofg, dlssg, nvngxfg, fsrfg, upscaler, fsrfg30",
        "Enabled=auto",
        "FGInput=auto",
        "FGOutput=auto",
        "",
        "[FSR]",
        "; 0 = FSR 4.0.2 | 1 = FSR 3.1.5 | 2 = FSR 2.3.4",
        "UpscalerIndex=auto",
        "FGIndex=auto",
        "Fsr4ForceModel=auto",
        "",
        "[OptiFG]",
        "HUDFix=auto",
        "",
        "[Spoofing]",
        "; 0x10de = Nvidia | 0x8086 = Intel | 0x1002 = AMD",
        "Dxgi=auto",
        "Vulkan=auto",
        "",
        "[Log]",
        "; Default (auto) is false",
        "LogToFile=auto",
        "LogLevel=auto",
        "SingleFile=auto",
        "",
        "[ProcessFilter]",
        "; Default (auto) is disabled (injects into all processes)",
        "TargetProcessName=auto",
        "ProcessExclusionList=auto",
        "",
    });

    public static string Create(MockFileSystem fs, string payloadRoot, string version = DefaultVersion)
    {
        foreach (var relative in RelativeFiles)
        {
            var full = fs.Path.Combine(payloadRoot, relative);
            var content = relative == IniFileName
                ? SampleIniContent
                : $"SYNTHETIC::{relative}::v{version}";
            fs.AddFile(full, new MockFileData(Encoding.UTF8.GetBytes(content)));
        }

        return payloadRoot;
    }

    public static FakeFileVersionReader CreateVersionReader(
        string payloadRoot, IFileSystem fs, string version = DefaultVersion)
    {
        var reader = new FakeFileVersionReader();
        reader.SetFileVersion(fs.Path.Combine(payloadRoot, OptiScalerDllName), version);
        return reader;
    }
}

/// <summary>Test double for PE version resources, which cannot be read from MockFileSystem.</summary>
public sealed class FakeFileVersionReader : IFileVersionReader
{
    private readonly Dictionary<string, string?> _original = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _version = new(StringComparer.OrdinalIgnoreCase);

    public void SetOriginalFilename(string path, string? value) => _original[path] = value;

    public void SetFileVersion(string path, string? value) => _version[path] = value;

    public string? GetOriginalFilename(string path) =>
        _original.TryGetValue(path, out var value) ? value : null;

    public string? GetFileVersion(string path) =>
        _version.TryGetValue(path, out var value) ? value : null;
}
