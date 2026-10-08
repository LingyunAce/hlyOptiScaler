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

    [Fact]
    public void Chinese_text_is_written_readable_not_escaped()
    {
        var json = DeployerJson.Serialize(new PlanWarning(WarningSeverity.Caution, "身份 DLL 全部冲突"));

        Assert.Contains("身份 DLL 全部冲突", json);
        Assert.DoesNotContain("\\u", json);
    }

    // 这些名字是 manifest.json 与 ledger.json 的**落盘契约**：改个成员名或改拼写，
    // 旧 manifest 就反序列化不出来；BackupStore.ReadJson 会把它吞成 null；RollbackExecutor
    // 接着报「读不到备份 manifest，可能已损坏或被删」—— 一次纯重构就这样静默且永久地
    // 毁掉用户的回滚能力，而报错信息还指错了方向。
    // 真要改，必须同时给 SchemaVersion 加兼容分支，并有意识地更新本测试。
    [Theory]
    [InlineData(typeof(PayloadOrigin), "localBuild", "gitHubRelease", "manualDirectory")]
    [InlineData(typeof(DllIdentity), "dxgi", "winmm", "version", "dbghelp", "d3d12", "wininet", "winhttp", "asi")]
    [InlineData(typeof(IdentityStatus), "resolved", "requiresUserDecision", "noCandidateAvailable")]
    [InlineData(typeof(BackupReason), "overwriteGameFile", "overwritePreviousOpti", "iniModified")]
    [InlineData(typeof(InstallState), "installed", "healthy", "rolledBack", "needsReinstall", "failed")]
    public void Persisted_enum_wire_names_are_pinned(Type enumType, params string[] expectedWireNames)
    {
        var actualWireNames = Enum.GetValues(enumType)
            .Cast<object>()
            .Select(value => DeployerJson.Serialize(value).Trim('"'))
            .ToArray();

        Assert.Equal(expectedWireNames, actualWireNames);
    }
}
