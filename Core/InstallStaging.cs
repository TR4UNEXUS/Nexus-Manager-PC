using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Nexus.Pc;

// Stage an entire package before publishing it. A crash leaves either disposable
// staging or an immutable list of verified files whose publication can resume.
internal sealed class InstallStaging(string root,Action check)
{
    const string Base="nexus/pc-import/v1/";
    string At(string rel)=>Installer.LocalPath(root,Base+rel);
    internal string Temp=>At("install-stage/work.partial");
    static readonly Regex Name=new("^[tnm]-[0-9a-f]{32}-[0-9a-f]{64}\\z",RegexOptions.CultureInvariant);
    static void Need(bool ok){if(!ok)throw new IOException("USB install recovery found unexpected or changed files. The existing library was preserved.");}
    public static bool Pending(string root)=>new[]{"install-stage","install-ready","content.partial"}.Any(s=>Path.Exists(Installer.LocalPath(root,Base+s)));
    readonly List<string> entries=[];
    string Destination(string name){
        Need(Name.IsMatch(name));string id=name.Substring(2,32);
        return name[0]=='t'?At("tickets/"+id+".bin"):Installer.LocalPath(root,Installer.RelativeContentPath(id));
    }
    void Clear(string folder){
        string dir=At(folder);
        if(!Directory.Exists(dir))return;
        foreach(var child in Directory.EnumerateFileSystemEntries(dir)){
            var name=Path.GetFileName(child);
            Need(name=="plan" || name=="work.partial" || Name.IsMatch(name));
            check();File.Delete(At(folder+"/"+name));
        }
        check();Directory.Delete(dir,false);
    }
    public void Recover(){
        check();Clear("install-stage");
        if(Directory.Exists(At("install-ready"))){
            var plan=At("install-ready/plan");
            if(File.Exists(plan)){
                Need(new FileInfo(plan).Length<=2*1024*1024);
                var names=File.ReadAllLines(plan);
                Need(names.Length<=16384 && names.Distinct().Count()==names.Length && names.All(n=>Name.IsMatch(n)));
                // Validate the complete set before resuming a partially committed package.
                foreach(string name in names){
                    check();var src=At("install-ready/"+name);var dest=Destination(name);
                    bool staged=File.Exists(src),published=File.Exists(dest);Need(staged||published);
                    if(name[0]!='t')Need(name.AsSpan(2,32).SequenceEqual(name.AsSpan(35,32)));
                    void Verify(string path){
                        using var file=File.OpenRead(path);
                        if(name[0]=='t'){
                            Need(file.Length is >=96 and <=131168);Span<byte> header=stackalloc byte[32];file.ReadExactly(header);
                            Need(header[..4].SequenceEqual("NCP1"u8) && Packages.Hex(header[16..32])==name.Substring(2,32));file.Position=0;
                        }
                        Need(Packages.Hex(SHA256.HashData(file))==name[35..]);
                    }
                    if(staged)Verify(src);
                    if(published)Verify(dest);
                }
                Publish(names,true);
            }else Need(!Directory.EnumerateFileSystemEntries(At("install-ready")).Any());
            Clear("install-ready");
        }
        check();File.Delete(At("content.partial"));
    }
    public void Begin(){
        check();entries.Clear();Directory.CreateDirectory(At("install-stage"));
        using var marker=new FileStream(At("content.partial"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);marker.Flush(true);
    }
    public void Keep(char kind,string id,byte[] digest){
        string name=kind+"-"+id+"-"+Packages.Hex(digest);Need(Name.IsMatch(name));
        check();File.Move(Temp,At("install-stage/"+name));entries.Add(name);
    }
    public void Abort(){
        check();Clear("install-stage");
        if(!Directory.Exists(At("install-ready")))File.Delete(At("content.partial"));
    }
    public void Commit(){
        check();
        using(var f=new FileStream(At("install-stage/plan"),FileMode.CreateNew,FileAccess.Write,FileShare.None)){
            f.Write(System.Text.Encoding.ASCII.GetBytes(string.Join("\n",entries)));f.Flush(true);
        }
        Directory.Move(At("install-stage"),At("install-ready"));
        Publish(entries,false);
        Clear("install-ready");check();File.Delete(At("content.partial"));
    }
    void Publish(IEnumerable<string> names,bool recovery){
        var moved=new List<string>();
        try{
            foreach(var name in names){
                check();var src=At("install-ready/"+name);var dest=Destination(name);
                if(recovery && File.Exists(dest))continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(src,dest,false);moved.Add(name);
            }
        }catch when(!recovery){
            foreach(string name in moved.AsEnumerable().Reverse()){
                check();File.Move(Destination(name),At("install-ready/"+name),false);
            }
            check();Directory.Move(At("install-ready"),At("install-stage"));throw;
        }
    }
}
