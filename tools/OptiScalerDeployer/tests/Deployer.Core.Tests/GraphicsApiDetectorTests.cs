using Xunit;
using Deployer.Core.Domain;
using Deployer.Core.Inspection;

namespace Deployer.Core.Tests;

public sealed class GraphicsApiDetectorTests
{
    [Fact]
    public void DetectsApisWithoutProxyD3d12FalsePositive()
    {
        Assert.Equal(new[] { GraphicsApi.Dx11, GraphicsApi.Dx12, GraphicsApi.Vulkan },
            GraphicsApiDetector.Detect(["D3D11.DLL", "d3d12core.dll", "vulkan-1.dll"]));
        Assert.Equal(new[] { GraphicsApi.Dx12 }, GraphicsApiDetector.Detect(["d3d12.dll"]));
    }
}
