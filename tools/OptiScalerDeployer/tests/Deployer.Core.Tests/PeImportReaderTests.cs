using Xunit;
using Deployer.Core.Inspection;
using Deployer.TestData;

namespace Deployer.Core.Tests;

public sealed class PeImportReaderTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ReadsNormalAndDelayImports(bool pe32Plus, bool delay)
    {
        var path = Path.GetTempFileName();
        try
        {
            var fixture = pe32Plus
                ? PeFixtureBuilder.BuildAmd64(["d3d12.dll"], delay ? ["nvngx_dlss.dll"] : [])
                : PeFixtureBuilder.BuildX86(["d3d12.dll"], delay ? ["nvngx_dlss.dll"] : []);
            File.WriteAllBytes(path, fixture);
            var imports = new PeImportReader().ReadImports(path);
            Assert.Contains("d3d12.dll", imports);
            Assert.Equal(delay, imports.Contains("nvngx_dlss.dll"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsUnterminatedDllName()
    {
        var path = Path.GetTempFileName();
        try
        {
            var image = PeFixtureBuilder.BuildAmd64(["d3d11.dll"]);
            PeFixtureBuilder.WipeFirstImportName(image);
            File.WriteAllBytes(path, image);
            Assert.Throws<InvalidDataException>(() => new PeImportReader().ReadImports(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsTruncatedAndNonPeFiles()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0x4d, 0x5a]);
            Assert.ThrowsAny<Exception>(() => new PeImportReader().ReadImports(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsOutOfRangeRvaAndTruncatedDirectory(bool truncate)
    {
        var path = Path.GetTempFileName();
        try
        {
            var image = PeFixtureBuilder.BuildAmd64(["d3d11.dll"]);
            if (truncate) PeFixtureBuilder.TruncateImportDirectory(image); else PeFixtureBuilder.CorruptImportDirectoryRva(image);
            File.WriteAllBytes(path, image);
            Assert.ThrowsAny<Exception>(() => new PeImportReader().ReadImports(path));
        }
        finally { File.Delete(path); }
    }
}
