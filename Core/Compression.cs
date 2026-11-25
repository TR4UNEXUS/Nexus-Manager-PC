using System.Security.Cryptography;
using System.Text;
using LibHac.Common.Keys;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;

namespace Nexus.Pc;

public static class Compression
{
    public static void VerifyInput(Package package, CancellationToken token,Action<int>? advance=null)
    {
        using var file = File.OpenRead(package.Path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024*1024];
        foreach(var content in package.Files){
            token.ThrowIfCancellationRequested();
            if(content.Part.StoredSize!=0) Ncz.Decode(file,content.Part,(b,n)=>{digest.AppendData(b,0,n);advance?.Invoke(n);},token);
            else{
                file.Position=content.Part.Offset;
                for(long left=content.Part.Size;left>0;){
                    token.ThrowIfCancellationRequested();int count=(int)Math.Min(buffer.Length,left);
                    file.ReadExactly(buffer.AsSpan(0,count));digest.AppendData(buffer,0,count);left-=count;advance?.Invoke(count);
                }
            }
            var hash=digest.GetHashAndReset();
            if(Packages.Hex(hash.AsSpan(0,16))!=content.Id || (content.ExpectedHash!=null&&!hash.SequenceEqual(content.ExpectedHash)))
                throw new InvalidDataException("A game file failed its original content hash: "+content.Id);
        }
    }
    public static Dictionary<ulong,uint> ExpectedTitles(IEnumerable<Game> titles)
    {
        var expected = new Dictionary<ulong,uint>();
        foreach (var group in titles.GroupBy(g => g.FamilyId))
        {
            if (group.Count(g => g.Kind == "Base game") != 1 ||
                group.Count(g => g.Kind == "Update") > 1 ||
                group.Any(g => g.Kind is not ("Base game" or "Update" or "DLC")))
                throw new InvalidDataException("Choose one base and at most one matching update per game. Include any DLC you want to keep.");
            var game = group.Single(g => g.Kind == "Base game");
            if (game.Id != group.Key) throw new InvalidDataException("The base game has conflicting metadata.");
            expected.Add(game.Id, group.SingleOrDefault(g => g.Kind == "Update")?.Version ?? game.Version);
            foreach (var dlc in group.Where(g => g.Kind == "DLC"))
                if (!expected.TryAdd(dlc.Id, dlc.Version)) throw new InvalidDataException("Choose just one version of each DLC.");
        }
        if (expected.Count == 0) throw new InvalidDataException("Choose a base game to compress.");
        return expected;
    }

    public static void VerifyOutput(IReadOnlyList<Package> inputs, IReadOnlyList<Package> outputs)
    {
        var expected = ExpectedTitles(inputs.SelectMany(p => p.Games));
        var actual = outputs.SelectMany(p => p.Games).ToList();
        var originals = inputs.SelectMany(p => p.Games).Where(g => g.Kind != "Update").ToDictionary(g => g.Id);
        if (actual.Count != expected.Count || actual.Select(g => g.Id).Distinct().Count() != actual.Count ||
            actual.Any(g => !expected.TryGetValue(g.Id, out var v) || v != g.Version ||
                originals[g.Id].FamilyId != g.FamilyId || originals[g.Id].Kind != g.Kind))
            throw new InvalidDataException("The compressor did not produce the complete selection. A title may already be compressed. Use normal install for that title.");
        long bytes(IEnumerable<Package> list) => list.SelectMany(p => p.Files).DistinctBy(c => c.Id).Sum(c => c.Part.Size);
        if (bytes(outputs) >= bytes(inputs)) throw new InvalidDataException("Compression would not reduce the installed size. Use normal install.");
    }

    public sealed record Entry(string Name, long Length, string? Path = null, byte[]? Data = null, string? Hash = null);

    public static List<Entry> ReadEntries(string root, Family game, KeySet keys, CancellationToken token, Action check,
        IReadOnlyDictionary<string,Permission>? originals=null)
    {
        if (!game.Complete) throw new IOException("This game is incomplete. Restore the missing files first.");
        ExpectedTitles(game.Members.Select(m => m.Metadata.Game));
        var entries = new Dictionary<string,Entry>();
        var rights = new HashSet<string>();
        foreach (var member in game.Members)
        {
            foreach (var part in member.Metadata.InstalledContent)
                Add(part.Id, false, part.Size, Packages.Hex(part.Hash));
            Add(member.MetaId, true, null, null);
        }
        foreach (var right in rights)
        {
            var pair=CompressionPermissions.Find(root,right,token,originals)
                ?? throw new CompressionPermissionNeeded(right, "Select the original game, update or DLC package containing the missing permission");
            entries.Add(right+".tik", new(right+".tik",pair.Ticket.Length,Data:pair.Ticket));
            entries.Add(right+".cert", new(right+".cert",pair.Certificate.Length,Data:pair.Certificate));
        }

        if(entries.Count>8192)throw new IOException("This selection exceeds the package entry limit.");
        return entries.Values.ToList();

        void Add(string id, bool meta, long? expectedSize, string? expectedHash)
        {
            token.ThrowIfCancellationRequested(); check();
            var path = Installer.LocalPath(root, Installer.RelativeContentPath(id));
            var name = id + (meta ? ".cnmt.nca" : ".nca");
            if (entries.TryGetValue(name, out var previous)) {
                if (previous.Hash != expectedHash || (expectedSize != null && previous.Length != expectedSize))
                    throw new InvalidDataException("Two manifests disagree about a shared file.");
                return;
            }
            using var file = File.OpenRead(path); using var storage = new StreamStorage(file,true);
            var header = new Nca(keys,storage).Header;
            if (file.Length != header.NcaSize || (expectedSize != null && file.Length != expectedSize))
                throw new InvalidDataException("An installed file has the wrong size: " + name);
            if (header.HasRightsId) rights.Add(Packages.Hex(header.RightsId));
            entries.Add(name,new(name,file.Length,path,Hash:expectedHash));
        }
    }

    // A savings preview can report the base on its own when an add-on's permission
    // is unavailable. Snapshot and installation always require the complete selection.
    public static (List<Entry> Entries,bool BaseOnly) ReadPreviewEntries(string root,Family game,KeySet keys,
        CancellationToken token,Action check){
        try{return (ReadEntries(root,game,keys,token,check),false);}
        catch(CompressionPermissionNeeded) when(game.Members.Count>1){
            token.ThrowIfCancellationRequested();
            var baseGame=game.Members.Single(m=>m.Metadata.Type==0x80);
            return (ReadEntries(root,game with {Members=[baseGame]},keys,token,check),true);
        }
    }

    // Snapshot only the registered content referenced by this family. The source drive is read-only.
    public static void Snapshot(string root, Family game, KeySet keys, string output, CancellationToken token, Action check,
        IReadOnlyDictionary<string,Permission>? originals=null,IProgress<double>? progress=null)
    {
        var files=ReadEntries(root,game,keys,token,check,originals);
        int names = files.Sum(e => e.Name.Length + 1);
        int pad = (16 - ((16 + 24 * files.Count + names) % 16)) % 16;
        using var writer = new BinaryWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        writer.Write("PFS0"u8); writer.Write(files.Count); writer.Write(names + pad); writer.Write(0);
        long offset = 0; int name = 0;
        foreach (var e in files) {
            writer.Write(offset); writer.Write(e.Length); writer.Write(name); writer.Write(0);
            offset = checked(offset + e.Length); name += e.Name.Length + 1;
        }
        foreach (var e in files) { writer.Write(Encoding.ASCII.GetBytes(e.Name)); writer.Write((byte)0); }
        writer.Write(new byte[pad]);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();long last=-250,copied=0;
        long total=files.Where(e=>e.Path!=null).Sum(e=>e.Length);
        byte[] buffer = new byte[1024 * 1024];
        foreach (var e in files)
        {
            token.ThrowIfCancellationRequested(); check();
            if (e.Data != null) { writer.Write(e.Data); continue; }
            using var source = File.OpenRead(e.Path!);
            if (source.Length != e.Length) throw new IOException("An installed file changed size during compression.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long left = e.Length; left > 0;) {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(left, buffer.Length);
                source.ReadExactly(buffer.AsSpan(0,n)); writer.Write(buffer,0,n); hash.AppendData(buffer,0,n); left -= n;copied+=n;
                if(progress!=null&&(elapsed.ElapsedMilliseconds-last>=250||copied==total)){
                    progress.Report((double)copied/total);last=elapsed.ElapsedMilliseconds;
                }
            }
            string digest = Packages.Hex(hash.GetHashAndReset());
            if (!e.Name.StartsWith(digest[..32], StringComparison.Ordinal) || (e.Hash != null && digest != e.Hash))
                throw new InvalidDataException("An installed file failed its original content hash: " + e.Name);
        }
        check();
    }
}
