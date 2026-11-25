using System.Security.Cryptography;

namespace Nexus.Pc;

public record Activity(string Package, string Stage, long Done, long Total, double MiBPerSecond);
public record Outcome(int Written, int Reused, int Tickets, int Missing);

public static class Installer
{
    public static string RelativeContentPath(string id)
    {
        byte shard=SHA256.HashData(Convert.FromHexString(id))[0];
        return Path.Combine("registered",shard.ToString("X8"),id+".nca");
    }

    // Only app-generated components reach this function. Reparse points must
    // never redirect content writes or cleanup outside the selected volume.
    internal static string LocalPath(string root,string relative)
    {
        var path=root;
        foreach(var component in relative.Split(new[]{'/','\\'},StringSplitOptions.RemoveEmptyEntries))
        {
            if(component is "." or ".." || component.Contains(':'))throw new IOException("Invalid USB path.");
            path=Path.Combine(path,component);
            try { if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new IOException("A USB library path is a link. Select an ordinary volume."); }
            catch(FileNotFoundException){} catch(DirectoryNotFoundException){}
        }
        return path;
    }

    public static Outcome Write(string root,IReadOnlyList<Package> packages,IProgress<Activity> progress,CancellationToken token,Action? checkVolume=null,Action? checkLibrary=null)
    {
        checkVolume?.Invoke();
        root=Path.GetFullPath(root);
        var drive=new DriveInfo(Path.GetPathRoot(root)!);
        if(packages.Count==0)throw new InvalidDataException("Select at least one game package.");
        foreach(var p in packages.SelectMany(p=>p.Files))
            if(drive.DriveFormat=="FAT32" && p.Part.Size>uint.MaxValue)
                throw new IOException("A game file exceeds FAT32's 4 GiB limit. Select an exFAT USB drive.");

        string work=LocalPath(root,"nexus/pc-import/v1");
        System.IO.Directory.CreateDirectory(work);
        using var session=new FileStream(LocalPath(root,"nexus/pc-import/v1/session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        CompressedReplacement.Recover(root,checkVolume??(()=>{}));
        if(Library.NeedsRecovery(root))throw new IOException("Open USB library and finish the interrupted deletion before installing packages.");
        var staging=new InstallStaging(root,checkVolume??(()=>{}));staging.Recover();
        checkLibrary?.Invoke();
        var unique=packages.SelectMany(p=>p.Files).DistinctBy(c=>c.Id);
        long required=unique.Where(c=>!File.Exists(LocalPath(root,RelativeContentPath(c.Id)))).Sum(c=>c.Part.Size)
            +packages.SelectMany(p=>p.Tickets).Sum(t=>(long)t.Bundle.Length);
        if(required>drive.AvailableFreeSpace)throw new IOException("There is not enough free space for the selected packages.");
        long total=packages.Sum(p=>p.Files.Sum(c=>checked(c.Part.Size*2))), done=0;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        long last=-1000, stageStart=0, stageBytes=0;
        string package="",stage="";
        void Report(long bytes,bool force=false)
        {
            done+=bytes;stageBytes+=bytes;
            if(force||clock.ElapsedMilliseconds-last>=120)
            {
                last=clock.ElapsedMilliseconds;
                double speed=stageBytes/1048576d/Math.Max(.001,(last-stageStart)/1000d);
                progress.Report(new(package,stage,done,total,speed));
            }
        }
        void Start(string text){stage=text;stageBytes=0;stageStart=clock.ElapsedMilliseconds;Report(0,true);}
        byte[] buffer=new byte[1024*1024];
        byte[] Transfer(Stream from,Stream? to,long size)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for(long left=size;left>0;)
            {
                token.ThrowIfCancellationRequested();int count=(int)Math.Min(left,buffer.Length);
                from.ReadExactly(buffer.AsSpan(0,count));to?.Write(buffer,0,count);
                hash.AppendData(buffer,0,count);left-=count;Report(count);
            }
            return hash.GetHashAndReset();
        }
        static void Check(Content c,byte[] digest)
        {
            if(Packages.Hex(digest.AsSpan(0,16))!=c.Id || (c.ExpectedHash!=null&&!digest.SequenceEqual(c.ExpectedHash)))
                throw new InvalidDataException("Content verification failed for "+c.Id+". Existing files were kept.");
        }

        // The temporary name is exclusively owned under the library lock. A
        // retry after unplug or power loss can discard the interrupted file.
        string temp=staging.Temp;
        int written=0,reused=0,tickets=0,missing=0;
        try
        {
            foreach(var p in packages)
            {
                token.ThrowIfCancellationRequested();package=Path.GetFileName(p.Path);
                using var input=File.OpenRead(p.Path);
                staging.Begin();
                missing+=p.MissingTickets.Length;
                foreach(var t in p.Tickets)
                {
                    var final=LocalPath(root,"nexus/pc-import/v1/tickets/"+t.Id+".bin");
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                    if(File.Exists(final))
                    {
                        if(new FileInfo(final).Length!=t.Bundle.Length || !File.ReadAllBytes(final).SequenceEqual(t.Bundle))
                            throw new InvalidDataException("A different ticket for the same rights ID already exists on this drive. It was preserved.");
                        continue;
                    }
                    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(t.Bundle);output.Flush(true);}
                    if(!File.ReadAllBytes(temp).SequenceEqual(t.Bundle))throw new IOException("Ticket readback failed.");
                    staging.Keep('t',t.Id,SHA256.HashData(t.Bundle));tickets++;
                }
                foreach(var c in p.Files.OrderBy(c=>c.IsMeta))
                {
                    token.ThrowIfCancellationRequested();
                    var final=LocalPath(root,RelativeContentPath(c.Id));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                    if(File.Exists(final))
                    {
                        Start("Checking existing USB content");
                        using var existing=File.OpenRead(final);
                        if(existing.Length!=c.Part.Size)throw new IOException("An existing USB file has a different size: "+c.Id);
                        Check(c,Transfer(existing,null,existing.Length));
                        done+=c.Part.Size;reused++;continue;
                    }
                    Start("Writing "+(c.IsMeta?"game metadata":"game content"));
                    input.Position=c.Part.Offset;
                    byte[] expected;
                    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024))
                    {
                        if(c.Part.StoredSize>0){
                            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            Ncz.Decode(input,c.Part,(b,n)=>{output.Write(b,0,n);hash.AppendData(b,0,n);Report(n);},token);
                            expected=hash.GetHashAndReset();
                        }else expected=Transfer(input,output,c.Part.Size);
                        Check(c,expected);output.Flush(true);
                    }
                    Start("Verifying USB copy");
                    using(var read=File.OpenRead(temp))
                    {
                        if(read.Length!=c.Part.Size || !Transfer(read,null,read.Length).SequenceEqual(expected))
                            throw new IOException("USB readback failed for "+c.Id);
                    }
                    token.ThrowIfCancellationRequested();staging.Keep(c.IsMeta?'m':'n',c.Id,expected);written++;
                }
                token.ThrowIfCancellationRequested();staging.Commit();
                // A single-title package filename is a useful offline label if
                // the control archive's display name cannot be read on this PC.
                if(p.Games.Count==1 && p.Files.FirstOrDefault(c=>c.IsMeta) is Content meta){
                    checkVolume?.Invoke();
                    var hint=LocalPath(root,"nexus/pc-import/v1/names/"+meta.Id+".name");
                    try {
                        if(!File.Exists(hint)){
                            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(hint)!);
                            using var label=new FileStream(hint,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                            label.Write(System.Text.Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(p.Path)));
                        }
                    }catch(IOException){}catch(UnauthorizedAccessException){}
                }
            }
            checkVolume?.Invoke();Start("Ready for Switch");return new(written,reused,tickets,missing);
        }
        catch(Exception error){
            try{staging.Abort();}
            catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException){
                throw new IOException("Reconnect the same USB drive and reopen its library to finish install recovery.",new AggregateException(error,cleanup));
            }
            throw;
        }
    }
}
