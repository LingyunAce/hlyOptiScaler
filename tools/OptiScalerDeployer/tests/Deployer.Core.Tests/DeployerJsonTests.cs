using Deployer.Core.Domain;
using Deployer.Core.Serialization;
using Xunit;

namespace Deployer.Core.Tests;

public class DeployerJsonTests
{
    [Fact]
    public void Enums_serialize_as_readable_strings()
    {
        var json = DeployerJson.Serialize(DllIdentity.Winmm);

        Assert.Equal("\"winmm\"", json);
    }

    [Fact]
    public void GameTarget_roundtrips()
    {
        var target = new GameTarget(
            "Halo Infinite", GamePlatform.Xbox, "Microsoft.25438_8wekyb3d8bbwe!App", "App",
            @"C:\XboxGames\Halo Infinite\content", @"C:\XboxGames\Halo Infinite\content\content\Binaries\Win64",
            "HaloInfinite-WinGDK-Shipping.exe", "a1b2c3d4");

        var restored = DeployerJson.Deserialize<GameTarget>(DeployerJson.Serialize(target));

        Assert.Equal(target, restored);
    }

    [Fact]
    public void Manifest_roundtrips()
    {
        var manifest = new DeploymentManifest
        {
            PlanId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            CreatedUtc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            Fingerprint = "a1b2c3d4",
            GameTitle = "Halo Infinite",
            DeployDirectory = @"C:\XboxGames\Halo Infinite\content",
            Payload = new PayloadInfo(PayloadOrigin.LocalBuild, "0.9.1.0", @"D:\repo\x64\Release\a"),
            Identity = new IdentityDecision(
                DllIdentity.Winmm, "winmm.dll", IdentityStatus.Resolved,
                new[] { DllIdentity.Dxgi }, null),
            Profile = new UpscalerProfile
            {
                Id = "amd-rdna4", DisplayName = "AMD RDNA4", Description = "RX 9070 XT",
                IsBuiltin = true,
                Edits = new[] { new IniEdit("Upscalers", "Dx12Upscaler", null, "ffx") },
            },
            Backups = new[] { new BackupEntry(@"C:\game\winmm.dll", @"C:\backup\winmm.dll", BackupReason.OverwriteGameFile) },
            WrittenPaths = new[] { @"C:\game\winmm.dll", @"C:\game\OptiScaler.ini" },
        };

        var restored = DeployerJson.Deserialize<DeploymentManifest>(DeployerJson.Serialize(manifest));

        JsonAssert.Equivalent(manifest, restored);
    }
}
