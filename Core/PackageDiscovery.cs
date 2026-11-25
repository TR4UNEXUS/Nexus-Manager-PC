namespace Nexus.Pc;

public sealed record QueuedFile(string Path,long Size);
public sealed record PackageScan(IReadOnlyList<QueuedFile> Files,int Unreadable,int AlreadyQueued);

public static class PackageDiscovery
{
    public static PackageScan Scan(IEnumerable<string> inputs, IEnumerable<string> queued, CancellationToken cancel = default)
    {
        var files = new List<QueuedFile>();
        var known = new HashSet<string>(queued.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(inputs.Reverse());
        int unreadable=0, duplicates=0;
        while (pending.TryPop(out var path))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var full = Path.GetFullPath(path);
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && new DirectoryInfo(full).LinkTarget != null) continue;
                    if (!visited.Add(Path.TrimEndingDirectorySeparator(full))) continue;
                    var children = new List<string>();
                    foreach (var entry in Directory.EnumerateFileSystemEntries(full))
                    {
                        cancel.ThrowIfCancellationRequested();
                        children.Add(entry);
                    }
                    children.Sort(StringComparer.OrdinalIgnoreCase);
                    for (var i=children.Count-1;i>=0;i--) pending.Push(children[i]);
                }
                else if (Packages.SupportedFile(full))
                {
                    if (known.Contains(full)) { duplicates++; continue; }
                    var size = new FileInfo(full).Length;
                    known.Add(full);
                    files.Add(new(full,size));
                }
            }
            catch(Exception e) when(e is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            { unreadable++; }
        }
        return new(files,unreadable,duplicates);
    }
}
