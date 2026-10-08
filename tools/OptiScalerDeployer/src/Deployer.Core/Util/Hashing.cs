using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Deployer.Core.Util;

public static class Hashing
{
    private const int HexLength = 64;

    /// <summary>返回小写十六进制 SHA256。经 IFileSystem 读，因此 MockFileSystem 上照样能算。</summary>
    public static string Sha256OfFile(IFileSystem fs, string path)
    {
        using var stream = fs.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256OfBytes(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256OfString(string text) =>
        Sha256OfBytes(Encoding.UTF8.GetBytes(text));

    /// <summary>取 SHA256 前 <paramref name="length"/> 位十六进制小写，用于短指纹。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 不在 [1, 64] 区间内。</exception>
    public static string ShortHash(string text, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, HexLength);
        return Sha256OfString(text)[..length];
    }
}
