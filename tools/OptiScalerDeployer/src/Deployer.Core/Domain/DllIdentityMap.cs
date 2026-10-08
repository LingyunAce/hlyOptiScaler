namespace Deployer.Core.Domain;

public static class DllIdentityMap
{
    private static readonly Dictionary<DllIdentity, string> FileNames = new()
    {
        [DllIdentity.Dxgi]     = "dxgi.dll",
        [DllIdentity.Winmm]    = "winmm.dll",
        [DllIdentity.Version]  = "version.dll",
        [DllIdentity.Dbghelp]  = "dbghelp.dll",
        [DllIdentity.D3d12]    = "d3d12.dll",
        [DllIdentity.Wininet]  = "wininet.dll",
        [DllIdentity.Winhttp]  = "winhttp.dll",
        [DllIdentity.Asi]      = "OptiScaler.asi",
    };

    public static string FileName(this DllIdentity identity) => FileNames[identity];
}
