using System.Text;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class AtomicFileWriterTests
{
    private const string Source = @"D:\payload\file.dll";
    private const string Destination = @"C:\game\OptiScaler\file.dll";
    private readonly MockFileSystem _fs = new();

    private FileWrite Copy(string content = "payload")
    {
        _fs.AddFile(Source, new MockFileData(Encoding.UTF8.GetBytes(content)));
        return new FileWrite(Source, Destination, Encoding.UTF8.GetByteCount(content), Hashing.Sha256OfString(content));
    }

    [Fact]
    public void Copies_only_after_checksum_matches()
    {
        var writer = new AtomicFileWriter(_fs);
        writer.Write(Copy());

        Assert.Equal("payload", _fs.File.ReadAllText(Destination));
        Assert.False(_fs.File.Exists(Destination + ".optideploy.tmp"));
    }

    [Fact]
    public void Writes_inline_ini_as_utf8_without_bom()
    {
        const string text = "[Upscalers]\r\nDx12Upscaler=ffx\r\n";
        var writer = new AtomicFileWriter(_fs);
        writer.Write(new FileWrite(Source, Destination, Encoding.UTF8.GetByteCount(text), Hashing.Sha256OfString(text), text));

        var bytes = _fs.File.ReadAllBytes(Destination);
        Assert.Equal(0x5B, bytes[0]);
        Assert.Equal(text, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Reports_only_newly_created_directories()
    {
        var created = new AtomicFileWriter(_fs).Write(Copy());

        Assert.Contains(@"C:\game", created, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"C:\game\OptiScaler", created, StringComparer.OrdinalIgnoreCase);
        _fs.AddFile(Destination + ".optideploy.tmp", new MockFileData("user file"));
        Assert.Throws<IOException>(() => new AtomicFileWriter(_fs).Write(Copy("second")));
        Assert.Equal("user file", _fs.File.ReadAllText(Destination + ".optideploy.tmp"));
    }

    [Fact]
    public void Existing_file_is_replaced_and_no_bak_remains()
    {
        _fs.AddFile(Destination, new MockFileData("old original"));

        new AtomicFileWriter(_fs).Write(Copy());

        Assert.Equal("payload", _fs.File.ReadAllText(Destination));
        Assert.False(_fs.File.Exists(Destination + ".optideploy.bak"));
    }

    [Fact]
    public void Checksum_failure_leaves_existing_destination_untouched()
    {
        _fs.AddFile(Source, new MockFileData("actual"));
        _fs.AddFile(Destination, new MockFileData("game original"));
        var wrong = new FileWrite(Source, Destination, 6, Hashing.Sha256OfString("other"));

        Assert.Throws<ChecksumMismatchException>(() => new AtomicFileWriter(_fs).Write(wrong));

        Assert.Equal("game original", _fs.File.ReadAllText(Destination));
        Assert.False(_fs.File.Exists(Destination + ".optideploy.tmp"));
    }

    [Fact]
    public void Length_mismatch_leaves_existing_destination_untouched()
    {
        _fs.AddFile(Source, new MockFileData("payload"));
        _fs.AddFile(Destination, new MockFileData("original"));
        var wrongLength = new FileWrite(Source, Destination, 99, Hashing.Sha256OfString("payload"));

        Assert.Throws<InvalidDataException>(() => new AtomicFileWriter(_fs).Write(wrongLength));

        Assert.Equal("original", _fs.File.ReadAllText(Destination));
    }

    [Fact]
    public void Missing_payload_throws_without_touching_destination()
    {
        var write = new FileWrite(Source, Destination, 1, Hashing.Sha256OfString("x"));

        Assert.Throws<FileNotFoundException>(() => new AtomicFileWriter(_fs).Write(write));
        Assert.False(_fs.File.Exists(Destination));
    }
}
