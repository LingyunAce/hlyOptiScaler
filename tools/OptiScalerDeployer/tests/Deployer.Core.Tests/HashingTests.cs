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
}
