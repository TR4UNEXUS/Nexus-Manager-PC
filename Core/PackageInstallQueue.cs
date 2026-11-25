namespace Nexus.Pc;

public sealed record InstallFailure(string Path, string Stage, string Reason);

public sealed class InstallDestinationException(string message, Exception? cause = null) : IOException(message, cause) { }

public sealed class InstallGroup(List<Package> packages)
{
    public List<Package> Packages { get; } = packages;
    public string Stage { get; set; } = "Installing on USB";
}

public sealed class PackageInstallQueue(IReadOnlyList<string> paths)
{
    public List<InstallFailure> Failures { get; } = [];
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Remaining => paths.Count - Installed.Count - Failures.Count;

    public static string Reason(Exception error) => error switch
    {
        EndOfStreamException => "The file ended before all of its data could be read. Check that the source package is complete.",
        FileNotFoundException e when e.FileName != null => $"File not found: {e.FileName}",
        UnauthorizedAccessException => "Windows denied access. " + error.Message,
        _ => string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message
    };

    public static bool CanReport(Exception e) =>
        e is not (OperationCanceledException or InstallDestinationException or OutOfMemoryException or AccessViolationException);

    public List<Package> Inspect(Func<string, Package> read, CancellationToken token)
    {
        var good = new List<Package>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try { good.Add(read(path)); }
            catch (Exception error) when (CanReport(error))
            {
                token.ThrowIfCancellationRequested();
                Failures.Add(new(path, "Checking package", Reason(error)));
            }
        }
        return good;
    }

    public static List<InstallGroup> Groups(List<Package> packages, bool compress)
    {
        if (!compress) return packages.Select(p => new InstallGroup([p])).ToList();

        // Multi-title packages can connect otherwise separate base/update/DLC sets.
        var sets = new Dictionary<ulong, HashSet<ulong>>();
        foreach (var package in packages)
        {
            var ids = package.Games.Select(g => g.FamilyId).ToHashSet();
            foreach (var related in ids.Where(sets.ContainsKey).Select(id => sets[id]).Distinct().ToArray())
                ids.UnionWith(related);
            foreach (var id in ids) sets[id] = ids;
        }
        return packages.GroupBy(p => sets[p.Games[0].FamilyId])
            .Select(group => new InstallGroup(group.ToList())).ToList();
    }

    public async Task<bool> TryGroup(InstallGroup group, Func<Task> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await action(); return true; }
        catch (Exception error) when (CanReport(error))
        {
            token.ThrowIfCancellationRequested();
            var reason = Reason(error);
            if (group.Packages.Count > 1)
                reason = "This compression group did not finish. " + reason;
            foreach (var p in group.Packages) Failures.Add(new(p.Path, group.Stage, reason));
            return false;
        }
    }

    public async Task InstallAsync(IReadOnlyList<InstallGroup> groups, Func<InstallGroup,int,Task> install,
        Action checkDestination, CancellationToken token)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            checkDestination();
            var group = groups[i];
            try
            {
                if (await TryGroup(group, () => install(group, i), token))
                    foreach (var p in group.Packages) Installed.Add(p.Path);
            }
            catch (InstallDestinationException e)
            {
                foreach (var p in group.Packages) Failures.Add(new(p.Path, group.Stage, Reason(e)));
                throw;
            }
        }
    }

    public string Report(string? stopped)
    {
        var lines = new List<string> { $"Installed: {Installed.Count}    Failed: {Failures.Count}    Not completed: {Remaining}" };
        if (stopped != null) lines.Add(stopped);
        foreach (var failure in Failures)
        {
            lines.Add("");
            lines.Add(failure.Path);
            lines.Add($"{failure.Stage} — {failure.Reason}");
        }
        if (Remaining > 0)
        {
            lines.Add("");
            lines.Add("Not completed:");
            var failed = Failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lines.AddRange(paths.Where(path => !Installed.Contains(path) && !failed.Contains(path)));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
