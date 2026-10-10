using System.Reflection.PortableExecutable;

namespace Deployer.Core.Inspection;

/// <summary>Reads normal and delay-load DLL imports from a PE image using checked RVA mapping.</summary>
public sealed record PeImportData(Machine Machine, bool IsManaged, bool IsPe32Plus, bool IsDll, bool IsExecutable, IReadOnlySet<string> Imports);

public sealed class PeImportReader
{
    private const int MaxDirectoryBytes = 16 * 1024 * 1024;
    private const int MaxImports = 65536;
    private const int MaxNameLength = 4096;

    public PeImportData Read(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new InvalidDataException("PE 文件流必须支持读取和定位。");
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var headers = pe.PEHeaders;
        if (headers.PEHeader is null || headers.CoffHeader.Machine is not (Machine.Amd64 or Machine.I386 or Machine.Arm64))
            throw new InvalidDataException("文件不是受支持的 PE 可执行映像。");

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in headers.SectionHeaders)
            if (section.VirtualAddress < 0 || section.PointerToRawData < 0 || section.SizeOfRawData < 0)
                throw new InvalidDataException("PE 节头包含负数偏移或长度。");
        ReadDirectory(stream, headers, headers.PEHeader.ImportTableDirectory, false, result);
        ReadDirectory(stream, headers, headers.PEHeader.DelayImportTableDirectory, true, result);
        return new PeImportData(headers.CoffHeader.Machine, headers.CorHeader is not null,
            headers.PEHeader.Magic == PEMagic.PE32Plus,
            (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0,
            (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) != 0, result);
    }

    public IReadOnlySet<string> ReadImports(string path) { using var stream = File.OpenRead(path); return Read(stream).Imports; }

    private static void ReadDirectory(Stream stream, PEHeaders headers, DirectoryEntry entry, bool delay, HashSet<string> result)
    {
        if (entry.RelativeVirtualAddress == 0 && entry.Size == 0) return;
        if (entry.RelativeVirtualAddress == 0 || entry.Size == 0 || entry.Size > MaxDirectoryBytes)
            throw new InvalidDataException("PE 导入目录长度或 RVA 无效。");
        var stride = delay ? 32 : 20;
        if (entry.Size < stride) throw new InvalidDataException("PE 导入目录被截断。");
        var (start, available) = MapRva(headers, checked((uint)entry.RelativeVirtualAddress), stream.Length);
        if ((ulong)entry.Size > available) throw new InvalidDataException("PE 导入目录越过节的原始数据范围。");
        var terminated = false;
        var imports = 0;
        Span<byte> descriptor = stackalloc byte[32];
        for (var cursor = 0; cursor <= entry.Size - stride; cursor += stride)
        {
            if (++imports > MaxImports) throw new InvalidDataException("PE 导入目录条目过多。");
            stream.Position = checked(start + cursor);
            stream.ReadExactly(descriptor[..stride]);
            var zero = true;
            for (var i = 0; i < stride; i++) if (descriptor[i] != 0) { zero = false; break; }
            if (zero) { terminated = true; break; }

            uint nameRva;
            if (delay)
            {
                var attributes = U32(descriptor, 0);
                var name = U32(descriptor, 4);
                nameRva = (attributes & 1) != 0 ? name : VaToRva(name, headers.PEHeader!.ImageBase);
            }
            else nameRva = U32(descriptor, 12);
            result.Add(ReadAsciiZ(stream, headers, nameRva));
        }
        if (!terminated) throw new InvalidDataException("PE 导入目录缺少终止描述符或被截断。");
    }

    private static uint VaToRva(uint address, ulong imageBase)
    {
        if (address < imageBase || (ulong)address - imageBase > uint.MaxValue) throw new InvalidDataException("PE 延迟导入名称地址溢出。");
        return (uint)((ulong)address - imageBase);
    }

    private static (long Offset, ulong Available) MapRva(PEHeaders headers, uint rva, long fileLength)
    {
        var matches = new List<(long Offset, ulong Available)>();
        foreach (var section in headers.SectionHeaders)
        {
            if (rva < (uint)section.VirtualAddress) continue;
            var delta = (ulong)rva - (uint)section.VirtualAddress;
            if (delta < (uint)section.SizeOfRawData)
            {
                var raw = (ulong)(uint)section.PointerToRawData + delta;
                if (raw >= (ulong)fileLength) throw new InvalidDataException("PE RVA 映射超出文件范围。");
                matches.Add((checked((long)raw), Math.Min((ulong)(uint)section.SizeOfRawData - delta, (ulong)fileLength - raw)));
            }
        }
        if (matches.Count > 1) throw new InvalidDataException("PE RVA 映射到重叠的多个节。");
        if (matches.Count == 1) return matches[0];
        if (rva < (uint)headers.PEHeader!.SizeOfHeaders && (ulong)rva < (ulong)fileLength)
            return (rva, Math.Min((ulong)(uint)headers.PEHeader.SizeOfHeaders - rva, (ulong)fileLength - rva));
        throw new InvalidDataException("PE RVA 不属于任何原始节。");
    }

    private static string ReadAsciiZ(Stream stream, PEHeaders headers, uint rva)
    {
        var (offset, available) = MapRva(headers, rva, stream.Length);
        var limit = (int)Math.Min((ulong)MaxNameLength, available);
        if (limit <= 0) throw new InvalidDataException("PE 导入 DLL 名称位于无效 RVA。");
        var bytes = new byte[limit]; stream.Position = offset; var count = 0; var terminated = false;
        while (count < limit)
        {
            var value = stream.ReadByte();
            if (value < 0) break;
            if (value == 0) { terminated = true; break; }
            if (value > 0x7f) throw new InvalidDataException("PE 导入 DLL 名称不是 ASCII。");
            bytes[count++] = (byte)value;
        }
        if (count == 0 || !terminated) throw new InvalidDataException("PE 导入 DLL 名称为空、过长或未终止。");
        return System.Text.Encoding.ASCII.GetString(bytes, 0, count);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
}
