using System.Buffers.Binary;
using System.IO.Abstractions;
using System.Text;

namespace Deployer.Core.GameDiscovery;

/// <summary>Read-only helpers for the two Xbox game-library layouts.</summary>
internal static class XboxGameLibrary
{
    private const int HeaderLength = 8;
    private const int MaxMarkerBytes = 2048;
    private static readonly byte[] Magic = "RGBX"u8.ToArray();
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);

    public static bool TryReadGamingRoot(IFileSystem fs, string driveRoot, out string? libraryRoot, out string? error)
    {
        libraryRoot = null;
        error = null;
        string markerPath;
        string fullDrive;
        try
        {
            fullDrive = fs.Path.GetFullPath(driveRoot);
            markerPath = fs.Path.Combine(fullDrive, ".GamingRoot");
            if (!fs.File.Exists(markerPath)) return false;
            if ((fs.File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            {
                error = "marker 是重解析文件";
                return false;
            }

            using var stream = fs.FileStream.New(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderLength + 4 || stream.Length > MaxMarkerBytes || ((stream.Length - HeaderLength) & 1) != 0)
            {
                error = "marker 长度无效";
                return false;
            }

            var bytes = new byte[checked((int)stream.Length)];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) { error = "marker 内容不完整"; return false; }
                read += count;
            }
            if (stream.ReadByte() != -1) { error = "marker 在读取期间发生变化"; return false; }
            if (!bytes.AsSpan(0, 4).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 1)
            {
                error = "marker header 或版本不受支持";
                return false;
            }

            var payloadLength = bytes.Length - HeaderLength;
            if (bytes[^1] != 0 || bytes[^2] != 0)
            {
                error = "marker 路径缺少 UTF-16 结束符";
                return false;
            }
            string relative;
            try { relative = StrictUtf16Le.GetString(bytes, HeaderLength, payloadLength - 2); }
            catch (DecoderFallbackException) { error = "marker 路径不是有效 UTF-16LE"; return false; }
            if (string.IsNullOrWhiteSpace(relative) || relative[0] == '\uFEFF' || relative.Contains('\0'))
            {
                error = "marker 路径为空或含有无效字符";
                return false;
            }

            var segments = relative.Split(['\\', '/'], StringSplitOptions.None);
            if (fs.Path.IsPathRooted(relative) || relative.Contains(':') || segments.Any(s =>
                    s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ')
                    || s.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || s.Any(char.IsControl)))
            {
                error = "marker 必须指向本盘内的相对目录";
                return false;
            }

            var candidate = fs.Path.GetFullPath(fs.Path.Combine(fullDrive, relative));
            var drive = fs.Path.GetPathRoot(fullDrive);
            if (string.IsNullOrWhiteSpace(drive) || !IsStrictDescendant(fs, drive, candidate))
            {
                error = "marker 路径越出盘符根目录或指向盘符根目录本身";
                return false;
            }
            if (!fs.Directory.Exists(candidate)) return false;
            if (!HasNoReparseAncestor(fs, drive, candidate, out error)) return false;

            libraryRoot = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsRecognizedLibraryRoot(IFileSystem fs, string path)
    {
        try
        {
            var full = fs.Path.GetFullPath(path);
            var drive = fs.Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(drive) || !fs.Directory.Exists(full)) return false;
            var defaultRoot = fs.Path.GetFullPath(fs.Path.Combine(drive, "XboxGames"));
            if (SamePath(fs, full, defaultRoot)) return HasNoReparseAncestor(fs, drive, full, out _);
            return TryReadGamingRoot(fs, drive, out var custom, out _) && custom is not null && SamePath(fs, full, custom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public static bool IsGameContent(IFileSystem fs, string gameDirectory, string contentDirectory,
        CancellationToken ct, Action<string> warn)
    {
        try
        {
            if (!fs.Directory.Exists(contentDirectory)
                || (fs.File.GetAttributes(contentDirectory) & FileAttributes.ReparsePoint) != 0
                || (fs.File.GetAttributes(gameDirectory) & FileAttributes.ReparsePoint) != 0)
                return false;

            var hasManifest = HasFile(fs, contentDirectory, "MicrosoftGame.config")
                || HasFile(fs, contentDirectory, "appxmanifest.xml")
                || HasFile(fs, gameDirectory, "MicrosoftGame.config")
                || HasFile(fs, gameDirectory, "appxmanifest.xml");
            return hasManifest && ExecutableFinder.Find(fs, contentDirectory, ct, warn).Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    public static bool HasTopLevelExecutable(IFileSystem fs, string directory)
    {
        try { return fs.Directory.GetFiles(fs.Path.GetFullPath(directory)).Any(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static bool HasNoReparseAncestor(IFileSystem fs, string driveRoot, string path, out string? error)
    {
        error = null;
        var drive = fs.Path.GetFullPath(driveRoot);
        var current = fs.Path.GetFullPath(path);
        if (!IsStrictDescendant(fs, drive, current)) { error = "Xbox 库不在盘符根目录内"; return false; }
        while (true)
        {
            try
            {
                if ((fs.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                { error = $"路径祖先是重解析点：{current}"; return false; }
            }
            catch (FileNotFoundException) { error = $"路径不存在：{current}"; return false; }
            catch (DirectoryNotFoundException) { error = $"路径不存在：{current}"; return false; }
            catch (IOException ex) { error = ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; return false; }

            if (SamePath(fs, current, drive)) break;
            var parent = fs.Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || SamePath(fs, parent, current))
            { error = "无法验证 Xbox 库的盘符祖先"; return false; }
            current = parent;
        }
        return true;
    }

    public static bool IsStrictDescendant(IFileSystem fs, string root, string path)
    {
        try
        {
            var fullRoot = fs.Path.GetFullPath(root);
            var fullPath = fs.Path.GetFullPath(path);
            var relative = fs.Path.GetRelativePath(fullRoot, fullPath);
            return relative != "." && relative != ".."
                && !relative.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + fs.Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                && !fs.Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return false; }
    }

    private static bool HasFile(IFileSystem fs, string root, string fileName)
    {
        var path = fs.Path.Combine(root, fileName);
        return fs.File.Exists(path) && (fs.File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    private static bool SamePath(IFileSystem fs, string first, string second)
    {
        try { return string.Equals(GameLibrary.Normalize(fs.Path.GetFullPath(first)), GameLibrary.Normalize(fs.Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
