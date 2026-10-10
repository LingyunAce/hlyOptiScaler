namespace Deployer.Core.Inspection;

public static class GraphicsApiDetector
{
    public static IReadOnlyList<Domain.GraphicsApi> Detect(IEnumerable<string> imports)
    {
        var names = new HashSet<string>(imports, StringComparer.OrdinalIgnoreCase);
        var apis = new List<Domain.GraphicsApi>();
        if (names.Contains("d3d11.dll")) apis.Add(Domain.GraphicsApi.Dx11);
        if (names.Contains("d3d12.dll") || names.Contains("d3d12core.dll")) apis.Add(Domain.GraphicsApi.Dx12);
        if (names.Contains("vulkan-1.dll")) apis.Add(Domain.GraphicsApi.Vulkan);
        return apis;
    }
}
