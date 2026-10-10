using Deployer.Core.Domain;
using Deployer.Core.Planning;
using Xunit;

namespace Deployer.Core.Tests;

public class FingerprintCalculatorTests
{
    private readonly FingerprintCalculator _calc = new();

    [Fact]
    public void Returns_eight_lowercase_hex_chars() =>
        Assert.Matches("^[0-9a-f]{8}$", _calc.Compute(
            GamePlatform.Xbox, "Microsoft.25438_8wekyb3d8bbwe!App",
            @"C:\XboxGames\Halo Infinite\content", "HaloInfinite-WinGDK-Shipping.exe"));

    [Fact]
    public void Is_stable_across_calls()
    {
        var one = _calc.Compute(GamePlatform.Steam, null, @"D:\Steam\Game", "game.exe");
        var two = _calc.Compute(GamePlatform.Steam, null, @"D:\Steam\Game", "game.exe");
        Assert.Equal(one, two);
    }

    [Fact]
    public void Ignores_case_and_trailing_separators_in_path_and_executable()
    {
        var one = _calc.Compute(GamePlatform.Steam, null, @"D:\Games\Foo", "foo.exe");
        var two = _calc.Compute(GamePlatform.Steam, null, @"d:\games\foo\", "FOO.EXE");
        Assert.Equal(one, two);
    }

    [Fact]
    public void Uses_package_family_name_instead_of_install_root()
    {
        var first = _calc.Compute(GamePlatform.Xbox, "PFN.A", @"C:\XboxGames\A", "a.exe");
        var samePackage = _calc.Compute(GamePlatform.Xbox, "pfn.a", @"D:\Elsewhere\A", "A.EXE");
        var otherPackage = _calc.Compute(GamePlatform.Xbox, "PFN.B", @"C:\XboxGames\A", "a.exe");
        Assert.Equal(first, samePackage);
        Assert.NotEqual(first, otherPackage);
    }

    [Fact]
    public void Platform_and_executable_name_are_part_of_fingerprint()
    {
        var xbox = _calc.Compute(GamePlatform.Xbox, null, @"C:\Games\A", "a.exe");
        Assert.NotEqual(xbox, _calc.Compute(GamePlatform.Manual, null, @"C:\Games\A", "a.exe"));
        Assert.NotEqual(xbox, _calc.Compute(GamePlatform.Xbox, null, @"C:\Games\A", "launcher.exe"));
    }

    [Fact]
    public void Rejects_blank_identity_and_executable_inputs()
    {
        Assert.Throws<ArgumentException>(() => _calc.Compute(GamePlatform.Steam, null, " ", "game.exe"));
        Assert.Throws<ArgumentException>(() => _calc.Compute(GamePlatform.Steam, null, @"C:\Game", ""));
        Assert.Throws<ArgumentException>(() => _calc.Compute(GamePlatform.Steam, " ", " ", "game.exe"));
    }
}
