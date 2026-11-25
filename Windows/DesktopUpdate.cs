using System.Diagnostics;
using System.Text.Json;
using Hats.Core;
using Microsoft.Win32;

namespace Nexus.Desktop;

internal sealed record DesktopUpdate(Version Version, PackAsset Installer)
{
    internal const string Repository = "TR4UNEXUS/Nexus-Manager-PC";
    internal const string AssetName = "Nexus-Manager-PC-win-x64-Setup.exe";
    internal static readonly Version Installed = typeof(DesktopUpdate).Assembly.GetName().Version!;
    internal static string Display(Version v) => v.Revision > 0 ? v.ToString() : v.ToString(3);

    internal static DesktopUpdate? Find(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!System.Version.TryParse(number, out var parsed) || parsed.Build < 0 || parsed.ToString() != number)
            throw new InvalidDataException("GitHub returned an unrecognised desktop version.");
        static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0,v.Build), Math.Max(0,v.Revision));
        if (Normalize(parsed) <= Normalize(installed)) return null;
        string url = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName ||
                asset.GetProperty("state").GetString() != "uploaded" ||
                asset.GetProperty("browser_download_url").GetString() != url) continue;
            long size = asset.GetProperty("size").GetInt64();
            string? digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
            if (size is <= 0 or > 1073741824 || digest is not { Length: 71 } ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !digest[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("The Windows installer has no valid download size or SHA-256 checksum.");
            return new(parsed, new(asset.GetProperty("id").GetInt64(), AssetName, url, size, digest, ""));
        }
        throw new InvalidDataException("The Windows installer is missing from this release.");
    }

    internal void Launch(string installer)
    {
        if (!System.Version.TryParse(FileVersionInfo.GetVersionInfo(installer).ProductVersion, out var payload) ||
            payload.Build < 0 || Display(payload) != Display(Version))
            throw new InvalidDataException("The downloaded installer version does not match the release.");
        var folder = Path.GetDirectoryName(Environment.ProcessPath)!;
        using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusManagerPCDesktop_is1");
        var managed = uninstall?.GetValue("InstallLocation") is string path &&
            string.Equals(Path.TrimEndingDirectorySeparator(path),folder,StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(installer) { UseShellExecute = false };
        foreach (var arg in new[]{"/SILENT","/SP-","/NORESTART","/NOCLOSEAPPLICATIONS","/NEXUSUPDATE=1",
            "/NEXUSPARENT="+Environment.ProcessId,"/DIR="+folder}) start.ArgumentList.Add(arg);
        if (!managed) start.ArgumentList.Add("/NEXUSPORTABLE=1");
        using var process = Process.Start(start) ?? throw new IOException("Windows could not start the installer.");
    }
}
