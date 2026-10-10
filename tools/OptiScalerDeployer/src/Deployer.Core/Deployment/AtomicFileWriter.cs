using System.IO.Abstractions;
using System.Text;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

/// <summary>Writes one verified file through a same-directory temporary file.</summary>
public sealed class AtomicFileWriter
{
    private const string TmpSuffix = ".optideploy.tmp";
    private const string BakSuffix = ".optideploy.bak";
    private readonly IFileSystem _fs;

    public AtomicFileWriter(IFileSystem fs) => _fs = fs;

    public IReadOnlyList<string> Write(FileWrite write)
    {
        var tmp = write.DestinationPath + TmpSuffix;
        var bak = write.DestinationPath + BakSuffix;
        if (_fs.File.Exists(tmp) || _fs.File.Exists(bak))
        {
            throw new IOException($"部署临时文件已存在，拒绝覆盖：{(_fs.File.Exists(tmp) ? tmp : bak)}");
        }

        var created = EnsureDirectories(write.DestinationPath);

        try
        {
            if (write.InlineContent is not null)
            {
                _fs.File.WriteAllText(tmp, write.InlineContent, new UTF8Encoding(false));
            }
            else
            {
                if (!_fs.File.Exists(write.SourcePath))
                {
                    throw new FileNotFoundException($"payload 文件不存在：{write.SourcePath}", write.SourcePath);
                }

                _fs.File.Copy(write.SourcePath, tmp);
            }

            var actual = Hashing.Sha256OfFile(_fs, tmp);
            var actualLength = _fs.FileInfo.New(tmp).Length;
            if (actualLength != write.Length)
            {
                throw new InvalidDataException($"文件长度不符：{tmp}，期望 {write.Length}，实际 {actualLength}。");
            }

            if (!string.Equals(actual, write.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChecksumMismatchException(tmp, write.Sha256, actual);
            }

            if (!_fs.File.Exists(write.DestinationPath))
            {
                _fs.File.Move(tmp, write.DestinationPath);
            }
            else
            {
                // File.Replace is the safety boundary. A failed replacement must leave the old
                // destination in place; deleting it and then moving the temp is not atomic.
                try
                {
                    _fs.File.Replace(tmp, write.DestinationPath, bak);
                }
                catch
                {
                    if (!_fs.File.Exists(write.DestinationPath) && _fs.File.Exists(bak))
                    {
                        _fs.File.Move(bak, write.DestinationPath);
                    }

                    throw;
                }

                TryDelete(bak);
            }

            return created;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Returns currently missing parent directories without creating them.</summary>
    public IReadOnlyList<string> MissingDirectories(string destinationPath)
    {
        var dir = _fs.Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(dir))
        {
            return Array.Empty<string>();
        }

        var missing = new List<string>();
        var current = dir;
        while (!string.IsNullOrEmpty(current) && !_fs.Directory.Exists(current))
        {
            missing.Insert(0, current);
            var parent = _fs.Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return missing;
    }

    private IReadOnlyList<string> EnsureDirectories(string destinationPath)
    {
        var missing = MissingDirectories(destinationPath);
        var dir = _fs.Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && missing.Count > 0)
        {
            _fs.Directory.CreateDirectory(dir);
        }

        return missing;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Leave cleanup artifacts visible; never mask the write failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave cleanup artifacts visible; never mask the write failure.
        }
    }
}
