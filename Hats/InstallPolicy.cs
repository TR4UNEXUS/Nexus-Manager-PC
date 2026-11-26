using System.Text.Json;

namespace Hats.Core;

public sealed record InstallPlan(string[] Replace, string[] Copy, string[] Directories, string[] Preserved, string[] Sysmodules, Dictionary<string, string> AutomaticallyPreserved);

public static class InstallPolicy
{
    public static string[] LoadPreserved(string config)
    {
        var paths = new HashSet<string>(Catalog.Current.Defaults.Concat(NexusProtection.MandatoryPaths), StringComparer.OrdinalIgnoreCase);
        if (File.Exists(config))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            if (!json.RootElement.TryGetProperty("preserve_paths", out var list) || list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The protection file must contain a preserve_paths list.");
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("Protected paths must be text.");
                var path = item.GetString()!; Paths.ValidateRelative(path); paths.Add(Paths.Key(path));
            }
        }
        return paths.Order().ToArray();
    }
    public static bool IsPack(string root) => File.Exists(Path.Combine(root, "atmosphere", "package3")) && Directory.Exists(Path.Combine(root, "switch")) && Directory.Exists(Path.Combine(root, "bootloader"));
    public static string FindPack(string root)
    {
        var matches = new[] { root }.Concat(Directory.EnumerateDirectories(root)).Where(IsPack).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The ZIP must contain one HATS pack with atmosphere/package3, bootloader and switch.");
    }
    public static InstallPlan Plan(string source, string sd, IEnumerable<string>? preserve = null)
    {
        source = Paths.Root(source); sd = Paths.Root(sd);
        if (Paths.Overlap(source, sd)) throw new IOException("The extracted pack and SD card must be separate folders.");
        if (!Directory.Exists(sd)) throw new IOException("The SD card is no longer connected.");
        if (!IsPack(source)) throw new InvalidDataException("Incomplete HATS pack: atmosphere/package3, bootloader and switch are required.");
        var identityFile = Paths.Checked(source, "atmosphere/nexus-pack.json");
        if (!File.Exists(identityFile)) throw new InvalidDataException("The selected pack is missing its Nexus HATS identity file.");
        using var identity = JsonDocument.Parse(File.ReadAllBytes(identityFile));
        if (identity.RootElement.ValueKind != JsonValueKind.Object ||
            !identity.RootElement.TryGetProperty("product", out var product) ||
            product.ValueKind != JsonValueKind.String || product.GetString() != "Nexus HATS")
            throw new InvalidDataException("The selected ZIP does not identify itself as a Nexus HATS pack.");

        var protectedPaths = new HashSet<string>(Catalog.Current.Defaults.Concat(NexusProtection.MandatoryPaths).Select(Paths.Key));
        foreach (var path in preserve ?? []) { Paths.ValidateRelative(path); protectedPaths.Add(Paths.Key(path)); }
        var replace = new List<string>();
        var copies = new List<string>();
        var directories = new List<string>();
        var modules = new List<string>();
        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Merge the official pack at the card root. Ownership comes from the release,
        // so a bundled app may use any author or filename in its homebrew metadata.
        void Scan(string folder)
        {
            foreach (var item in Paths.Children(folder))
            {
                var relative = Paths.Relative(source, item);
                var key = Paths.Key(relative);
                if (NexusProtection.IsUsbLibraryData(key) || protectedPaths.Any(p => Paths.Under(key, p)))
                {
                    skipped.Add(relative, "protected card data");
                    continue;
                }
                Paths.Checked(source, relative);
                var target = Paths.Checked(sd, relative);
                if (Directory.Exists(item))
                {
                    if (File.Exists(target)) throw new IOException($"Pack directory conflicts with a file on the card: {relative}");
                    if (key.StartsWith("atmosphere/contents/", StringComparison.Ordinal) && key.Count(c => c == '/') == 2)
                    {
                        if (SysmoduleIdentity.IsModule(item, Path.GetFileName(key))) modules.Add(Path.GetFileName(key));
                        else if (Directory.Exists(target))
                        {
                            skipped.Add(relative, "existing game content or mods");
                            continue;
                        }
                    }
                    if (key.StartsWith("switch/", StringComparison.Ordinal) && key.Count(c => c == '/') == 1 &&
                        Directory.Exists(target) && AppIdentifier.GameFolderReason(target) is string reason)
                    {
                        skipped.Add(relative, reason);
                        continue;
                    }
                    directories.Add(relative);
                    Scan(item);
                }
                else
                {
                    if (Directory.Exists(target) || protectedPaths.Any(p => Paths.Under(p, key)))
                        throw new IOException($"Pack file conflicts with a directory or protected path: {relative}");
                    if (File.Exists(target)) replace.Add(relative);
                    copies.Add(relative);
                }
            }
        }
        Scan(source);
        return new(replace.ToArray(), copies.ToArray(), directories.ToArray(), protectedPaths.Order().ToArray(), modules.ToArray(), skipped);
    }
}
