using System.Security.Cryptography;
using System.Text;

namespace Hats.Core;

public static class NexusProtection
{
    public static readonly string[] MandatoryPaths = ["atmosphere/usb_content", "atmosphere/usb_badges",
        "atmosphere/usb_content-repair.pending", "config/nexusmanager", "config/nexus-manager"];
    // Repair/recovery snapshots are siblings of the live index directory. These
    // namespaces belong to the current card, including incomplete transactions.
    public static bool IsUsbLibraryData(string path)
    {
        var key = Paths.Key(path);
        return Paths.Under(key, "atmosphere/usb_content") || Paths.Under(key, "atmosphere/usb_badges") ||
            Paths.Under(key, "atmosphere/usb_content-repair.pending") ||
            key.StartsWith("atmosphere/usb_content-repair-", StringComparison.Ordinal) ||
            key.StartsWith("atmosphere/usb_content-recovery-", StringComparison.Ordinal);
    }
    public static ExclusionSnapshot BundledSnapshot()
    {
        using var stream = typeof(NexusProtection).Assembly.GetManifestResourceStream("Hats.Core.hats-exclusions.json")
            ?? throw new IOException("The included Nexus protection list is missing. Reinstall the desktop app.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var json = reader.ReadToEnd();
        var paths = ExclusionService.Parse(json);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new(ReleaseService.Repository, ExclusionService.FileName, "bundled:" + digest, digest, DateTime.UtcNow, paths, json);
    }
    public static bool IsPackAsset(string name)
    {
        var lower = name.ToLowerInvariant();
        return !lower.StartsWith("nexus-hats-desktop") && !lower.StartsWith("nexus-hats-installer") &&
            !lower.StartsWith("nexus-usb-formatter") && !lower.StartsWith("nexus-manager") &&
            !lower.StartsWith("source.") && !lower.Contains("-source.") && !lower.Contains("-source-") &&
            !lower.EndsWith(".symbols.zip");
    }
}
