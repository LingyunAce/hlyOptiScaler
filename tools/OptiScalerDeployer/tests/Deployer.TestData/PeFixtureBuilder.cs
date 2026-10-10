using System.Buffers.Binary;

namespace Deployer.TestData;

/// <summary>Builds a small valid PE32/PE32+ executable with normal and optional delay imports.</summary>
public static class PeFixtureBuilder
{
    public static byte[] BuildAmd64(IEnumerable<string> imports, IEnumerable<string>? delayImports = null) => Build(0x8664, true, imports, delayImports, false);
    public static byte[] BuildManagedAmd64(IEnumerable<string> imports) => Build(0x8664, true, imports, null, true);
    public static byte[] BuildX86(IEnumerable<string> imports, IEnumerable<string>? delayImports = null) => Build(0x014c, false, imports, delayImports, false);
    public static byte[] BuildAmd64Pe32(IEnumerable<string> imports) => Build(0x8664, false, imports, null, false);

    private static byte[] Build(ushort machine, bool pe32Plus, IEnumerable<string> imports, IEnumerable<string>? delays, bool managed)
    {
        var normal = imports.ToArray(); var delay = (delays ?? []).ToArray();
        const int pe = 0x80, headers = 0x200, raw = 0x200, rva = 0x1000, rawSize = 0x4000;
        var data = new byte[headers + rawSize];
        W16(data, 0, 0x5a4d); W32(data, 0x3c, pe); W32(data, pe, 0x00004550);
        W16(data, pe + 4, machine); W16(data, pe + 6, 1); W16(data, pe + 20, (ushort)(pe32Plus ? 240 : 224)); W16(data, pe + 22, 0x0002);
        var opt = pe + 24; W16(data, opt, (ushort)(pe32Plus ? 0x20b : 0x10b));
        W32(data, opt + 32, 0x1000); W32(data, opt + 36, 0x200); W32(data, opt + 56, 0x5000); W32(data, opt + 60, headers); W16(data, opt + 68, 3);
        if (pe32Plus) W64(data, opt + 24, 0x140000000); else W32(data, opt + 28, 0x400000);
        var dirs = opt + (pe32Plus ? 112 : 96); W32(data, dirs - 4, 16);
        var section = opt + (pe32Plus ? 240 : 224); System.Text.Encoding.ASCII.GetBytes(".rdata").CopyTo(data, section);
        W32(data, section + 8, rawSize); W32(data, section + 12, rva); W32(data, section + 16, rawSize); W32(data, section + 20, raw);
        W32(data, section + 36, 0x40000040);
        var cursor = raw;
        if (normal.Length > 0)
        {
            var table = cursor; cursor += (normal.Length + 1) * 20;
            W32(data, dirs + 8, (uint)(rva + table - raw)); W32(data, dirs + 12, (uint)((normal.Length + 1) * 20));
            foreach (var name in normal)
            {
                W32(data, table + 12, (uint)(rva + cursor - raw));
                System.Text.Encoding.ASCII.GetBytes(name + "\0").CopyTo(data, cursor); cursor += name.Length + 1; table += 20;
            }
        }
        if (delay.Length > 0)
        {
            var table = cursor; cursor += (delay.Length + 1) * 32;
            W32(data, dirs + 13 * 8, (uint)(rva + table - raw)); W32(data, dirs + 13 * 8 + 4, (uint)((delay.Length + 1) * 32));
            foreach (var name in delay)
            {
                W32(data, table, 1); W32(data, table + 4, (uint)(rva + cursor - raw));
                System.Text.Encoding.ASCII.GetBytes(name + "\0").CopyTo(data, cursor); cursor += name.Length + 1; table += 32;
            }
        }
        if (managed)
        {
            var cli = cursor; cursor += 72;
            W32(data, dirs + 14 * 8, (uint)(rva + cli - raw)); W32(data, dirs + 14 * 8 + 4, 72);
            W32(data, cli, 72); W16(data, cli + 4, 2); W16(data, cli + 6, 5);
        }
        return data;
    }

    public static void WipeFirstImportName(byte[] image)
    {
        var pe = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3c, 4));
        var opt = pe + 24; var is64 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(opt, 2)) == 0x20b;
        var dirs = opt + (is64 ? 112 : 96); var importRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dirs + 8, 4));
        var raw = FindRawForRva(image, opt, is64, importRva);
        var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(raw + 12, 4));
        var nameRaw = FindRawForRva(image, opt, is64, nameRva);
        Array.Fill(image, (byte)'A', nameRaw, Math.Min(4096, image.Length - nameRaw));
    }

    public static void CorruptImportDirectoryRva(byte[] image, uint rva = uint.MaxValue)
    {
        var pe = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3c, 4));
        var opt = pe + 24; var is64 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(opt, 2)) == 0x20b;
        var dirs = opt + (is64 ? 112 : 96);
        W32(image, dirs + 8, rva);
    }

    public static void TruncateImportDirectory(byte[] image, uint size = 8)
    {
        var pe = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3c, 4));
        var opt = pe + 24; var is64 = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(opt, 2)) == 0x20b;
        var dirs = opt + (is64 ? 112 : 96);
        W32(image, dirs + 12, size);
    }

    private static int FindRawForRva(byte[] data, int opt, bool is64, uint target)
    {
        var section = opt + (is64 ? 240 : 224); var va = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section + 12, 4));
        var ptr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section + 20, 4)); return checked((int)(ptr + target - va));
    }
    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
    private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o, 8), v);
}
