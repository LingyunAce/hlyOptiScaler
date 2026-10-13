using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deployer.Core.Elevation;

/// <summary>Verifies the on-disk Worker package before it is started with elevation.</summary>
public static class WorkerTrust
{
    private const uint FileReadAttributes = 0x80;
    private const uint ReadControl = 0x00020000;
    private const uint FileShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint DaclSecurityInformation = 0x4;
    private const int SeFileObject = 1;
    private const uint FileReadData = 0x1;
    private const uint FileWriteData = 0x2;
    private const uint FileAppendData = 0x4;
    private const uint FileWriteEa = 0x10;
    private const uint FileDeleteChild = 0x40;
    private const uint FileWriteAttributes = 0x100;
    private const byte InheritOnlyAce = 0x08;
    private const uint Delete = 0x10000;
    private const uint WriteDac = 0x40000;
    private const uint WriteOwner = 0x80000;
    private const uint GenericAll = 0x10000000;
    private const uint GenericWrite = 0x40000000;
    private const uint WriteMask = FileWriteData | FileAppendData | FileWriteEa | FileDeleteChild | FileWriteAttributes | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    private const uint AccessAllowedAceType = 0;
    private const uint AccessDeniedAceType = 1;
    private static readonly HashSet<string> TrustedSids = new(StringComparer.Ordinal)
    {
        "S-1-5-18", // Local System
        "S-1-5-32-544", // Builtin Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };

    /// <summary>Checks the full package, then holds handles that deny replacement while it is running.</summary>
    public static IDisposable Acquire(string workerPath)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("无法在此系统验证提权 Worker 的 Windows ACL。");
        if (string.IsNullOrWhiteSpace(workerPath) || !Path.IsPathFullyQualified(workerPath)
            || !workerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("提权 Worker 路径无效。");

        var full = Path.GetFullPath(workerPath);
        if (!File.Exists(full)) throw new FileNotFoundException("找不到提权 Worker。", full);
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
        if (!roots.Any(root => IsContained(root, full)))
            throw new InvalidDataException("提权 Worker 必须安装在受保护目录中；请不要从用户可写目录运行便携版。");

        var held = new List<SafeFileHandle>();
        try
        {
            var directory = Path.GetDirectoryName(full)!;
            var directories = Ancestors(directory).Reverse().ToArray();
            foreach (var path in directories)
            {
                ElevationPolicy.RejectReparseAncestors(path);
                var handle = Open(path, directory: true);
                held.Add(handle);
                if ((GetAttributes(handle) & FileAttributeReparsePoint) != 0) throw new InvalidDataException("Worker 目录不能是重解析点。");
                ValidateSecurity(handle, allowCreateEntries: !path.Equals(directory, StringComparison.OrdinalIgnoreCase));
            }

            if (!File.Exists(full)) throw new FileNotFoundException("Worker 在验证期间发生变化。", full);
            var packageFiles = new List<string>();
            LockPackageTree(directory, held, packageFiles);
            if (!packageFiles.Any(file => file.Equals(full, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Worker 文件不在受保护的程序目录中。");
            return new HandleLease(held);
        }
        catch
        {
            foreach (var handle in held) handle.Dispose();
            throw;
        }
    }

    // Kept pure so the ACL policy can be regression tested without changing machine ACLs.
    public static void ValidateAclRules(string ownerSid, IReadOnlyList<WorkerTrustAce> aces, bool allowCreateEntries = false)
    {
        if (!TrustedSids.Contains(ownerSid)) throw new InvalidDataException("Worker 路径或文件的所有者不受信任。");
        foreach (var ace in aces)
        {
            if (!ace.IsAllow || ace.InheritOnly) continue;
            var writeMask = allowCreateEntries ? WriteMask & ~(FileWriteData | FileAppendData) : WriteMask;
            if ((ace.AccessMask & writeMask) != 0 && !TrustedSids.Contains(ace.Sid))
                throw new InvalidDataException("Worker 路径或文件允许非系统管理员写入。");
        }
    }

    private static void ValidateSecurity(SafeFileHandle handle, bool allowCreateEntries = false)
    {
        var error = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
            out var owner, out _, out var dacl, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, "无法读取 Worker 安全描述符。");
        try
        {
            if (owner == IntPtr.Zero || dacl == IntPtr.Zero) throw new InvalidDataException("Worker ACL 缺失，拒绝提权。");
            if (!ConvertSidToStringSid(owner, out var ownerString)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Worker 所有者。");
            try
            {
                var parsed = new List<WorkerTrustAce>();
                if (!GetAclInformation(dacl, out var acl, (uint)Marshal.SizeOf<AclSizeInformation>(), 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Worker ACL。");
                for (uint index = 0; index < acl.AceCount; index++)
                {
                    if (!GetAce(dacl, index, out var ace)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Worker ACL 条目。");
                    var type = Marshal.ReadByte(ace);
                    var flags = Marshal.ReadByte(ace, 1);
                    if (type == AccessDeniedAceType) continue;
                    if (type != AccessAllowedAceType)
                    {
                        // Audit and mandatory-label entries do not grant access. Unknown ACE types fail closed.
                        if (type is 2 or 3 or 17 or 18) continue;
                        throw new InvalidDataException("Worker ACL 含无法安全解释的授权条目。");
                    }
                    var mask = unchecked((uint)Marshal.ReadInt32(ace, 4));
                    var sid = IntPtr.Add(ace, 8);
                    if (!ConvertSidToStringSid(sid, out var sidString)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析 Worker ACL 条目。");
                    try { parsed.Add(new WorkerTrustAce(Marshal.PtrToStringUni(sidString)!, mask, true, (flags & InheritOnlyAce) != 0)); }
                    finally { LocalFree(sidString); }
                }
                ValidateAclRules(Marshal.PtrToStringUni(ownerString)!, parsed, allowCreateEntries);
            }
            finally { LocalFree(ownerString); }
        }
        finally { LocalFree(descriptor); }
    }

    private static SafeFileHandle Open(string path, bool directory)
    {
        var handle = CreateFile(path, (directory ? FileReadAttributes : FileReadData) | ReadControl, FileShareRead, IntPtr.Zero, OpenExisting,
            (directory ? FileFlagBackupSemantics : 0) | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法锁定 Worker 程序文件。"); }
        return handle;
    }

    private static uint GetAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法验证 Worker 文件类型。");
        return info.FileAttributes;
    }

    private static void LockPackageTree(string directory, List<SafeFileHandle> held, List<string> files)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ElevationPolicy.RejectReparseAncestors(path);
            var isDirectory = Directory.Exists(path);
            var handle = Open(path, isDirectory);
            held.Add(handle);
            if ((GetAttributes(handle) & FileAttributeReparsePoint) != 0) throw new InvalidDataException("Worker 包不能包含重解析点。");
            ValidateSecurity(handle);
            if (isDirectory) LockPackageTree(path, held, files);
            else files.Add(path);
        }
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current)) { yield return current; var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)); if (parent == current) break; current = parent ?? string.Empty; }
    }

    private static bool IsContained(string root, string child) => child.Equals(root, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class HandleLease(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AclSizeInformation { public uint AceCount; public uint AclBytesInUse; public uint AclBytesFree; }
    [StructLayout(LayoutKind.Sequential)] private struct ByHandleFileInformation { public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime; public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks; public uint FileIndexHigh; public uint FileIndexLow; }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetAclInformation(IntPtr acl, out AclSizeInformation info, uint infoLength, int informationClass);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetAce(IntPtr acl, uint aceIndex, out IntPtr ace);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed record WorkerTrustAce(string Sid, uint AccessMask, bool IsAllow, bool InheritOnly = false);
