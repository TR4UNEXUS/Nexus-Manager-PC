using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Hats.Core;

public static class SysmoduleIdentity
{
    public static bool IsModule(string folder, string id)
    {
        if (id.Length != 16 || !ulong.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected)) return false;
        var nsp = Path.Combine(folder, "exefs.nsp");
        if (!File.Exists(nsp) || Paths.Linked(nsp)) return false;
        // Existing reviewed modules include HOC/sys-clk. Their entire folder is
        // managed, including resources, not treated as a game's mod directory.
        if (Catalog.Current.Modules.ContainsKey(id.ToLowerInvariant())) return true;
        // Application title IDs belong to games/homebrew applications, not an
        // automatically managed system service, even if its metadata is patched.
        if (expected >= 0x0100000000010000UL && expected <= 0x01FFFFFFFFFFFFFFUL) return false;
        try
        {
            using var file = File.OpenRead(nsp);
            if (file.Length < 16) return false;
            var header = Read(file, 0, 16);
            if (!header.AsSpan(0, 4).SequenceEqual("PFS0"u8)) return false;
            var count = U32(header, 4); var stringsSize = U32(header, 8);
            if (count is < 2 or > 64 || stringsSize is < 1 or > 65536) return false;
            long tableSize = checked(24L * count + stringsSize), dataStart = 16 + tableSize;
            if (dataStart > file.Length) return false;
            var table = Read(file, 16, (int)tableSize);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var regions = new List<(long Start, long End)>();
            byte[]? npdm = null; bool main = false;
            for (int i = 0; i < count; ++i)
            {
                int record = i * 24; var offset = U64(table, record); var size = U64(table, record + 8); var nameAt = U32(table, record + 16);
                if (nameAt >= stringsSize || offset > (ulong)(file.Length - dataStart) || size > (ulong)(file.Length - dataStart) - offset) return false;
                int start = checked((int)(count * 24 + nameAt)); int end = Array.IndexOf(table, (byte)0, start);
                if (end <= start) return false;
                var name = Encoding.UTF8.GetString(table, start, end - start);
                if (name.Contains('/') || name.Contains('\\') || name.Contains(':') || !names.Add(name)) return false;
                long first = dataStart + (long)offset, last = first + (long)size;
                if (regions.Any(r => first < r.End && r.Start < last)) return false;
                regions.Add((first, last));
                if (name == "main") main = size >= 0x100 && Read(file, first, 4).AsSpan().SequenceEqual("NSO0"u8);
                if (name == "main.npdm")
                {
                    if (size is < 0x80 or > 1048576) return false;
                    npdm = Read(file, first, (int)size);
                }
            }
            return main && npdm != null && IsSystemNpdm(npdm, expected);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException or ArgumentException) { return false; }
    }
    static byte[] Read(FileStream file, long offset, int size)
    {
        file.Position = offset; var result = new byte[size]; file.ReadExactly(result); return result;
    }
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
    static bool IsSystemNpdm(byte[] b, ulong expected)
    {
        if (!b.AsSpan(0, 4).SequenceEqual("META"u8)) return false;
        uint offset = U32(b, 0x70), size = U32(b, 0x74);
        if (offset < 0x80 || size < 0x40 || offset > b.Length || size > b.Length - offset) return false;
        int aci = (int)offset;
        if (!b.AsSpan(aci, 4).SequenceEqual("ACI0"u8) || U64(b, aci + 0x10) != expected) return false;
        uint kac = U32(b, aci + 0x30), bytes = U32(b, aci + 0x34);
        if (kac < 0x40 || kac > size || bytes > size - kac || bytes % 4 != 0) return false;
        bool seen = false;
        for (int n = 0; n < bytes; n += 4)
        {
            uint cap = U32(b, aci + (int)kac + n);
            // Atmosphere loader: trailing 13 one bits identify program type;
            // bits 14..16 are 0=system, 1=application, 2=applet. Missing=system.
            if ((cap & 0x3FFF) != 0x1FFF) continue;
            if (seen || (cap >> 14 & 7) != 0) return false;
            seen = true;
        }
        return true;
    }
}
