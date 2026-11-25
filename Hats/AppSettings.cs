using System.Text.Json;
using Hats.Core;

namespace Hats.WinUI;

public sealed class AppSettings
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusHATSDesktop.WinUI");
    public static string LogPath => Path.Combine(DataFolder, "installer.log");
    static readonly object LogLock = new();
    public static string DefaultBackupFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NexusHATSBackups");
    public string BackupFolder { get; set; } = DefaultBackupFolder;
    public static string ProtectionFile
    {
        get
        {
            var sidecar = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "preserve-paths.json");
            return File.Exists(sidecar) ? sidecar : Path.Combine(DataFolder, "preserve-paths.json");
        }
    }
    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(DataFolder, "settings.json"))) ?? new();
            var previousDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NexusHATSBackups", "WinUI");
            if (string.IsNullOrWhiteSpace(settings.BackupFolder) || Path.TrimEndingDirectorySeparator(settings.BackupFolder).Equals(previousDefault, StringComparison.OrdinalIgnoreCase))
                settings.BackupFolder = DefaultBackupFolder;
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save() { Directory.CreateDirectory(DataFolder); File.WriteAllText(Path.Combine(DataFolder, "settings.json"), JsonSerializer.Serialize(this)); }
    public static void Log(string message)
    {
        lock (LogLock)
        try
        {
            Directory.CreateDirectory(DataFolder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 4 * 1048576) File.Move(LogPath, LogPath + ".previous", true);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
