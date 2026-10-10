using Deployer.Core.Domain;

namespace Deployer.Core.Inspection;

public sealed class UpscalerSdkDetector
{
    private static readonly string[] ProxyNames = ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "d3d12.dll", "optiscaler.asi"];

    public IReadOnlyList<UpscalerSdk> Detect(IEnumerable<string> imports, IEnumerable<string> gameFiles)
    {
        var names = new HashSet<string>(imports.Select(Path.GetFileName).Where(n => n is not null)!, StringComparer.OrdinalIgnoreCase);
        foreach (var file in gameFiles)
        {
            var name = Path.GetFileName(file);
            if (name is not null && !ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        var detected = new List<UpscalerSdk>();
        bool Has(Func<string, bool> predicate) => names.Any(predicate);
        var hasSlDlss = Has(IsStreamlineUpscaler);
        var hasSlInterposer = Has(n => n.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase));
        if (Has(n => n.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("_nvngx.dll", StringComparison.OrdinalIgnoreCase)) || hasSlDlss) detected.Add(UpscalerSdk.Dlss);
        if (Has(n => n.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("libxess_dx11.dll", StringComparison.OrdinalIgnoreCase))) detected.Add(UpscalerSdk.XeSS);
        if (Has(n => n.Equals("ffx_fsr2_api_x64.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("ffx_fsr2_api_dx12_x64.dll", StringComparison.OrdinalIgnoreCase))) detected.Add(UpscalerSdk.Fsr2);
        if (Has(n => n.Equals("ffx_fsr3upscaler_x64.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("ffx_backend_dx12_x64.dll", StringComparison.OrdinalIgnoreCase))) detected.Add(UpscalerSdk.Fsr3);
        if (Has(n => n.StartsWith("amd_fidelityfx_", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) detected.Add(UpscalerSdk.FfxApi);
        if (hasSlDlss && hasSlInterposer) detected.Add(UpscalerSdk.Streamline);
        return detected;
    }

    private static bool IsStreamlineUpscaler(string name) => name.StartsWith("sl.dlss", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("sl.dlss_g", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("sl.dlssg", StringComparison.OrdinalIgnoreCase);
}
