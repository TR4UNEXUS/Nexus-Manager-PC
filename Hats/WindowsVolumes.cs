using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hats.Core;

public sealed record VolumeIdentity(uint Serial, string Label, string FileSystem);
public sealed record SdVolume(string Root, VolumeIdentity Identity, long TotalBytes, long FreeBytes, StorageDeviceInfo? Device = null)
{
    public string DisplayName => Device?.IsHekateUsb == true
        ? Device.DisplayName + (string.IsNullOrWhiteSpace(Identity.Label) ? "" : " · " + Identity.Label)
        : !string.IsNullOrWhiteSpace(Identity.Label) ? Identity.Label
        : !string.IsNullOrWhiteSpace(Device?.DisplayName) ? Device.DisplayName : "Removable drive (no label)";
    public string DeviceDescription => Device?.IsHekateUsb == true ? "Hekate USB storage: " + Device.Product
        : !string.IsNullOrWhiteSpace(Device?.HardwareName) ? "Device: " + Device.HardwareName : "Check the drive letter before installing.";
    public override string ToString() => $"{Root}  ·  {DisplayName}  ·  {TotalBytes / 1073741824d:N1} GB · {Identity.FileSystem}";
}

[SupportedOSPlatform("windows")]
public static class WindowsVolumes
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string root, StringBuilder label, int labelSize, out uint serial, out uint maximum, out uint flags, StringBuilder fileSystem, int fileSystemSize);
    [DllImport("shell32.dll")] public static extern bool IsUserAnAdmin();

    public static VolumeIdentity Identity(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1..] != ":\\") throw new IOException("Select the root of a removable drive.");
        if (root[..2].Equals(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase)) throw new IOException("The Windows system drive cannot be used.");
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Removable) throw new IOException("Only removable drives can be used. Reconnect your SD card and refresh the list.");
        if (!drive.IsReady) throw new IOException("The selected removable drive is not ready. Reconnect it and refresh the list.");
        var label = new StringBuilder(261); var fs = new StringBuilder(261);
        if (!GetVolumeInformation(root, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity)) throw new IOException("The selected drive is not ready. Reconnect it and refresh the list.");
        return new(serial, label.ToString(), fs.ToString());
    }
    public static void Validate(SdVolume volume)
    {
        if (Identity(volume.Root) != volume.Identity || new DriveInfo(volume.Root).TotalSize != volume.TotalBytes) throw new IOException("The selected drive has changed. Refresh the list and select it again.");
    }
    public static SdVolume[] Scan()
    {
        var result = new List<SdVolume>();
        foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                result.Add(new(drive.Name, Identity(drive.Name), drive.TotalSize, drive.AvailableFreeSpace, WindowsStorageDevices.Read(drive.Name)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result.ToArray();
    }
}

public sealed record Fat32Layout(int Cluster, uint FatSectors, byte[] Boot, byte[] Info, byte[] Fat)
{
    public static Fat32Layout Create(long length, int sectorSize = 512)
    {
        if (sectorSize != 512) throw new IOException("This formatter supports only 512-byte logical sectors.");
        long sectors = length / 512;
        if (length % 512 != 0 || sectors < 65536 || sectors > uint.MaxValue) throw new IOException("Unsupported FAT32 volume size (maximum below 2 TiB).");
        double mb = length / 1048576d; int cluster = mb <= 260 ? 1 : mb <= 8192 ? 8 : mb <= 16384 ? 16 : mb <= 32768 ? 32 : 64;
        long divisor = (256 * cluster + 2) / 2, fat = (sectors - 32 + divisor - 1) / divisor, count = (sectors - 32 - 2 * fat) / cluster;
        if (count < 65525 || count >= 0x0ffffff5 || fat * 128 < count + 2) throw new IOException("The volume cannot use a supported FAT32 layout.");
        var boot = new byte[512]; new byte[] { 0xeb, 0x58, 0x90 }.CopyTo(boot, 0); Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(boot, 3);
        static void U16(byte[] b, int p, ushort n) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), n);
        static void U32(byte[] b, int p, uint n) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), n);
        U16(boot, 11, 512); boot[13] = (byte)cluster; U16(boot, 14, 32); boot[16] = 2; boot[21] = 0xf8; U16(boot, 24, 63); U16(boot, 26, 255); U32(boot, 32, (uint)sectors);
        U32(boot, 36, (uint)fat); U32(boot, 44, 2); U16(boot, 48, 1); U16(boot, 50, 6); boot[64] = 0x80; boot[66] = 0x29; U32(boot, 67, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Encoding.ASCII.GetBytes("NO NAME    FAT32   ").CopyTo(boot, 71); boot[510] = 0x55; boot[511] = 0xaa;
        var info = new byte[512]; U32(info, 0, 0x41615252); U32(info, 484, 0x61417272); U32(info, 488, (uint)(count - 1)); U32(info, 492, 3); info[510] = 0x55; info[511] = 0xaa;
        var first = new byte[512]; U32(first, 0, 0x0ffffff8); U32(first, 4, 0x0fffffff); U32(first, 8, 0x0fffffff);
        return new(cluster, (uint)fat, boot, info, first);
    }
}

public interface IRawVolume : IDisposable
{
    void Control(uint code);
    (long Length, int SectorSize) Geometry();
    void Write(long sector, byte[] data);
    void Flush();
}

[SupportedOSPlatform("windows")]
public sealed class RawVolume : IRawVolume
{
    readonly SafeFileHandle handle;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushFileBuffers(SafeFileHandle file);
    public RawVolume(string root)
    {
        WindowsVolumes.Identity(root); handle = CreateFile(@"\\.\" + root[..2], 0xc0000000, 3, IntPtr.Zero, 3, 0x80000000, IntPtr.Zero); // FILE_FLAG_WRITE_THROUGH
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the card. Formatting requires administrator access."); }
    }
    public void Control(uint code) { if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, null, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public (long Length, int SectorSize) Geometry()
    {
        var geometry = new byte[1024]; var length = new byte[8];
        if (!DeviceIoControl(handle, 0x700a0, IntPtr.Zero, 0, geometry, (uint)geometry.Length, out var n, IntPtr.Zero) || n < 24 || !DeviceIoControl(handle, 0x7405c, IntPtr.Zero, 0, length, 8, out n, IntPtr.Zero) || n != 8) throw new IOException("Could not confirm the disk geometry. No format was performed.");
        return (BinaryPrimitives.ReadInt64LittleEndian(length), BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(20)));
    }
    public void Write(long sector, byte[] data) => RandomAccess.Write(handle, data, checked(sector * 512));
    public void Flush() { if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() => handle.Dispose();
}

public static class Fat32Formatter
{
    public const uint Lock = 0x90018, Unlock = 0x9001c, Dismount = 0x90020;
    // Keep the volume locked and mounted through writes/flush, then dismount to invalidate
    // the old filesystem before releasing the lock (the Windows format-volume sequence).
    public static void Format(IRawVolume device, Action<double>? progress = null)
    {
        bool locked = false, writing = false;
        Exception? failure = null;
        using (device)
        {
        try
        {
            device.Control(Lock); locked = true;
            var (length, sector) = device.Geometry(); var layout = Fat32Layout.Create(length, sector);
            writing = true;
            device.Write(0, new byte[32 * 512]); device.Write(0, layout.Boot); device.Write(1, layout.Info); device.Write(6, layout.Boot); device.Write(7, layout.Info);
            for (int index = 0; index < 2; index++)
            {
                long start = 32 + index * (long)layout.FatSectors; device.Write(start, layout.Fat);
                for (long offset = 1; offset < layout.FatSectors; offset += 2048)
                {
                    int count = (int)Math.Min(2048, layout.FatSectors - offset); device.Write(start + offset, new byte[count * 512]);
                    progress?.Invoke((index * (double)layout.FatSectors + offset + count) / (layout.FatSectors * 2d) * 95);
                }
            }
            device.Write(32 + 2L * layout.FatSectors, new byte[layout.Cluster * 512]);
            progress?.Invoke(95); device.Flush();
            progress?.Invoke(98);
        }
        catch (Exception e) { failure = e; }
        finally
        {
            // Also invalidate partially written metadata after an error. Preserve the original
            // write/flush error if cleanup fails, and always release the lock/handle.
            if (writing)
                try { device.Control(Dismount); }
                catch (Exception e) { if (failure == null) failure = e; else failure.Data["Dismount error"] = e.ToString(); }
            if (locked)
                try { device.Control(Unlock); }
                catch (Exception e) { if (failure == null) failure = e; else failure.Data["Unlock error"] = e.ToString(); }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        progress?.Invoke(100);
    }
}
