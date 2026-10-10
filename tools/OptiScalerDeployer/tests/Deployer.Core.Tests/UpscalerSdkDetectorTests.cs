using Xunit;
using Deployer.Core.Domain;
using Deployer.Core.Inspection;

namespace Deployer.Core.Tests;

public sealed class UpscalerSdkDetectorTests
{
    [Fact]
    public void RequiresRealGameFileAndExcludesOptiScalerProxyNames()
    {
        var detector = new UpscalerSdkDetector();
        Assert.Empty(detector.Detect(["sl.interposer.dll", "d3d12.dll"], []));
        Assert.Equal(new[] { UpscalerSdk.Dlss }, detector.Detect(["sl.interposer.dll"], ["nvngx_dlss.dll"]));
        Assert.Equal(new[] { UpscalerSdk.Dlss, UpscalerSdk.Streamline },
            detector.Detect(["sl.dlss.dll", "sl.interposer.dll"], []));
    }

    [Fact]
    public void DetectsFsrXeSsAndFfxVariants()
    {
        var found = new UpscalerSdkDetector().Detect([], ["libxess_dx11.dll", "ffx_fsr2_api_x64.dll", "ffx_fsr3upscaler_x64.dll", "amd_fidelityfx_loader_dx12.dll"]);
        Assert.Equal(new[] { UpscalerSdk.XeSS, UpscalerSdk.Fsr2, UpscalerSdk.Fsr3, UpscalerSdk.FfxApi }, found);
    }

    [Fact]
    public void DoesNotTreatFrameGenerationOnlyLibraryAsUpscalingSdk()
    {
        Assert.Empty(new UpscalerSdkDetector().Detect([], ["nvngx_dlssg.dll", "sl.dlss_g.dll"]));
    }

    [Fact]
    public void WildcardSdkNamesRequireDllSuffix()
    {
        Assert.Empty(new UpscalerSdkDetector().Detect([], [
            "amd_fidelityfx_note.txt", "amd_fidelityfx_dx12.dll.bak", "sl.dlss.txt", "sl.dlss.dll.bak", "sl.interposer.dll"]));
    }
}
