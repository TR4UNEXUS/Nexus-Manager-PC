using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hats.Core;

public sealed record StorageDeviceInfo(string Vendor, string Product, uint BusType)
{
    public bool IsHekateUsb => BusType == 7 && Vendor.Equals("hekate", StringComparison.OrdinalIgnoreCase);
    public string HardwareName => string.Join(" ", new[] { Vendor, Product }.Where(v => !string.IsNullOrWhiteSpace(v)));
    public string DisplayName => IsHekateUsb ? "Nintendo Switch (Hekate USB)" : HardwareName;

    // STORAGE_DEVICE_DESCRIPTOR has a fixed 36-byte header and bounded ASCII strings.
    public static StorageDeviceInfo? ParseDescriptor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 36) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (size < 36 || size > bytes.Length) return null;
        bytes = bytes[..(int)size];
        static string? ReadString(ReadOnlySpan<byte> data, uint offset)
        {
            if (offset == 0) return "";
            if (offset < 36 || offset >= data.Length) return null;
            var remaining = data[(int)offset..]; int end = remaining.IndexOf((byte)0);
            if (end < 0 || end > 256) return null;
            var value = remaining[..end];
            foreach (var c in value) if (c < 32 || c > 126) return null;
            return Encoding.ASCII.GetString(value).Trim();
        }
        var vendor = ReadString(bytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        var product = ReadString(bytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
        return vendor == null || product == null ? null : new(vendor, product, BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]));
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsStorageDevices
{
    const uint QueryProperty = 0x002D1400;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    public static StorageDeviceInfo? Read(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1..] != ":\\") return null;
        // Zero desired access: query device metadata only; no card reads/writes or locks.
        using var handle = CreateFile(@"\\.\" + root[..2], 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var query = new byte[12]; // StorageDeviceProperty / PropertyStandardQuery = 0.
        var header = new byte[8];
        if (!DeviceIoControl(handle, QueryProperty, query, (uint)query.Length, header, (uint)header.Length, out var returned, IntPtr.Zero) || returned < 8) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (size < 36 || size > 65536) return null;
        var descriptor = new byte[(int)size];
        if (!DeviceIoControl(handle, QueryProperty, query, (uint)query.Length, descriptor, size, out returned, IntPtr.Zero) || returned > size) return null;
        return StorageDeviceInfo.ParseDescriptor(descriptor.AsSpan(0, (int)returned));
    }
}
