using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class HashingTests
{
    [Fact]
    public void Sha256OfString_is_lowercase_hex_64_chars()
    {
        var hash = Hashing.Sha256OfString("OptiScaler");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    [Fact]
    public void Sha256OfString_is_stable_across_calls()
    {
        Assert.Equal(Hashing.Sha256OfString("abc"), Hashing.Sha256OfString("abc"));
    }

    [Fact]
    public void ShortHash_returns_requested_prefix_length()
    {
        var short8 = Hashing.ShortHash("xbox|c:\\xboxgames\\game|game.exe", 8);

        Assert.Equal(8, short8.Length);
        Assert.Equal(Hashing.Sha256OfString("xbox|c:\\xboxgames\\game|game.exe")[..8], short8);
    }

    [Fact]
    public void Sha256OfFile_matches_bytes_hash()
    {
        var fs = new MockFileSystem();
        const string path = @"C:\tmp\hash-me.bin";
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        fs.AddFile(path, new MockFileData(bytes));

        Assert.Equal(Hashing.Sha256OfBytes(bytes), Hashing.Sha256OfFile(fs, path));
    }

    // FIPS 180-2 的已知答案，一句断言同时钉住「算法 + UTF-8 编码 + 小写十六进制」三件事。
    // 之所以不能漂：GameTarget.Fingerprint 由 Sha256OfString 推出，并作为影子备份的目录名
    // 与台账索引键落盘；换掉编码会让所有既有备份静默失联 —— 用户看到「未安装」，
    // 而备份其实躺在另一个指纹底下。
    [Fact]
    public void Sha256OfString_matches_the_fips_known_answer()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                     Hashing.Sha256OfString("abc"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void ShortHash_rejects_length_outside_one_to_sixty_four(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Hashing.ShortHash("abc", length));
    }

    [Theory]
    [InlineData(1, "b")]
    [InlineData(64, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void ShortHash_accepts_the_boundary_lengths(int length, string expected)
    {
        Assert.Equal(expected, Hashing.ShortHash("abc", length));
    }
}
