using Nexus.Pc;
using LibHac.Common.Keys;

internal static class InstallQueueTests
{
    public static async Task Run(string root, string fixture, KeySet keys)
    {
        static void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); }
        var folder = Directory.CreateDirectory(Path.Combine(root, "queue-sources")).FullName;
        var usb = Directory.CreateDirectory(Path.Combine(root, "queue-volume")).FullName;
        string Copy(string name, string? source = null)
        {
            string path = Path.Combine(folder, name + ".nsp");
            File.Copy(source ?? fixture, path);
            return path;
        }
        var first = Copy("first");
        var corrupt = Copy("bad-header");
        using (var file = File.OpenWrite(corrupt)) file.Write("oops"u8);
        string secondFixture = Path.Combine(Path.GetDirectoryName(fixture)!, "shared.nsp");
        bool distinctPackage = File.Exists(secondFixture);
        var modified = Copy("bad-content", distinctPackage ? secondFixture : null);
        var last = Copy("last");
        var paths = new[] { first, corrupt, modified, last };
        var batch = new PackageInstallQueue(paths);
        var readable = batch.Inspect(path => Packages.Inspect(path, keys, default), default);
        Check(readable.Count == 3 && batch.Failures.Single().Path == corrupt, "Inspection should skip only the corrupt package");
        // Change the bytes after inspection to exercise the write/verification failure path.
        if (distinctPackage)
        {
            using var file = File.OpenWrite(modified);
            file.SetLength(0);
        }
        else File.Delete(modified);
        var visited = new List<string>();
        var groups = PackageInstallQueue.Groups(readable, false);
        await batch.InstallAsync(groups, (g, i) => {
            visited.Add(g.Packages[0].Path);
            Installer.Write(usb, g.Packages, new Watch(), default);
            return Task.CompletedTask;
        }, () => {}, default);
        Check(visited.SequenceEqual(new[] { first, modified, last }) && batch.Installed.SetEquals([first,last]), "The final package should install after a failed write");
        Check(batch.Failures.Count == 2 && batch.Remaining == 0 && batch.Failures.Last().Reason.Contains(distinctPackage ? "file ended" : "File not found"), "Write report should include a readable cause");
        var report = batch.Report(null);
        Check(report.Contains("Installed: 2") && report.Contains(corrupt) && report.Contains("Invalid package directory signature") && report.Contains(modified), "End report missing file or failure reason");
        Check(!InstallStaging.Pending(usb), "Failed package left a pending transaction");
        var complete = readable[0].Files.All(c => File.Exists(Path.Combine(usb, Installer.RelativeContentPath(c.Id))));
        Check(complete, "Completed content missing");

        Package Plan(string name, params ulong[] families) => new(name,families.Select(id => new Game(id,0,"Base game"){FamilyId=id}).ToList(),[],[],[]);
        var linked = new List<Package> { Plan("base",1),Plan("other",3),Plan("dlc",2),Plan("multi",1,2) };
        var grouped = PackageInstallQueue.Groups(linked,true);
        Check(grouped.Count == 2 && grouped[0].Packages.Select(p=>p.Path).SequenceEqual(new[]{"base","dlc","multi"}), "Compression split a related package or changed ordering");
        var compressed = new PackageInstallQueue(linked.Select(p=>p.Path).ToList());
        await compressed.InstallAsync(grouped, (_,i) => i==0 ? Task.FromException(new InvalidDataException("Could not compress file")) : Task.CompletedTask, () => {},default);
        Check(compressed.Failures.Count == 3 && compressed.Installed.SetEquals(["other"]), "One compression group blocked an unrelated title");

        var stop = new PackageInstallQueue(paths);
        using var cancel = new CancellationTokenSource();
        try
        {
            await stop.InstallAsync(groups, (g,i) => { if(i==1) cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; },()=>{},cancel.Token);
            throw new Exception("Cancellation ignored");
        } catch(OperationCanceledException) { }
        Check(stop.Installed.SetEquals([first]) && stop.Failures.Count==0,"Cancellation should preserve completed files");
        var unplug = new PackageInstallQueue(paths);
        try
        {
            await unplug.InstallAsync(groups, (g,i) => i==1 ? Task.FromException(new InstallDestinationException("USB disconnected")) : Task.CompletedTask,()=>{},default);
            throw new Exception("Continued after destination disconnected");
        } catch(InstallDestinationException) { }
        Check(unplug.Installed.SetEquals([first]) && unplug.Failures.Single().Path==modified && unplug.Remaining==2,"Drive failure lost queue status");
        Console.WriteLine("Install queue: mixed inspection/write failures, continuation, cleanup, compression groups, cancellation and destination loss passed.");
    }
}
