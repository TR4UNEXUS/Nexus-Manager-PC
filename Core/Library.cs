using System.Security.Cryptography;
using System.Text;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using Path=System.IO.Path;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;

namespace Nexus.Pc;

public record Member(string MetaId,Manifest Metadata)
{
    public string? DisplayVersion {get;init;}
    public List<string> Issues {get;init;}=[];
    public long Bytes {get;init;}
}
public enum CompressionState { Unknown, NoMarker, CompressedData, Rebuilt, CompressedBase, Compressed, NexusBase, NexusCompressed }
public record Family(ulong Id,string Name,List<Member> Members,long Bytes,bool Complete)
{
    public CompressionState Compression { get; init; }
    public byte[]? Icon {get;init;}
}
public record Inventory(List<Family> Games,int Unidentified,Dictionary<string,long> Files);
public record DeleteResult(int Files,long Bytes,int Shared,string Receipt);

public static class Library
{
    const string Sync="nexus/pc-import/v1/removals";
    static bool Hex(string s,int length)=>s.Length==length&&s.All(Uri.IsHexDigit);
    static string ContentPath(string root,string id)=>Installer.LocalPath(root,Installer.RelativeContentPath(id));
    static IEnumerable<string> AllIds(Member m)=>m.Metadata.References.Select(r=>r.Id).Prepend(m.MetaId);
    static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);}


    public static Inventory Scan(string root,KeySet keys,CancellationToken token,Action<string>? status=null,bool readControls=true)
    {
        var registered=Installer.LocalPath(root,"registered");
        Dictionary<string,long> files=[];
        List<Member> members=[];Dictionary<string,ControlInfo> controls=[];int unknown=0;
        var compressedContent=new HashSet<string>();
        if(!System.IO.Directory.Exists(registered))return new([],0,files);
        foreach(var folder in System.IO.Directory.EnumerateDirectories(registered)){
            token.ThrowIfCancellationRequested();
            if(!Hex(Path.GetFileName(folder),8))continue;
            Installer.LocalPath(root,Path.GetRelativePath(root,folder));
            foreach(var path in System.IO.Directory.EnumerateFiles(folder,"*.nca")){
                token.ThrowIfCancellationRequested();
                string id=Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if(!Hex(id,32)||!ContentPath(root,id).Equals(path,StringComparison.OrdinalIgnoreCase)){unknown++;continue;}
                if(!files.TryAdd(id,new FileInfo(path).Length)){unknown++;continue;}
                try {
                    using var input=File.OpenRead(path);
                    using var storage=new StreamStorage(input,true);
                    var nca=new Nca(keys,storage);
                    Need(nca.Header.NcaSize==input.Length,"Truncated NCA");
                    if(readControls && nca.Header.ContentType is NcaContentType.Program or NcaContentType.Data){
                        for(int i=0;i<4;i++)if(nca.Header.IsSectionEnabled(i)&&nca.Header.GetFsHeader(i).ExistsCompressionLayer()){
                            compressedContent.Add(id);break;
                        }
                    }
                    switch(nca.Header.ContentType){
                        case NcaContentType.Meta:
                            // Content identity must also authenticate the ownership manifest.
                            input.Position=0;
                            Need(Packages.Hex(SHA256.HashData(input)[..16])==id,"Metadata content ID mismatch");
                            members.Add(new(id,Packages.ReadManifest(nca,(nca.Header.TitleId>>56)==5)));break;
                        case NcaContentType.Control when readControls:
                            var control=ControlInfo.Read(nca);if(control!=null)controls[id]=control;break;
                    }
                }catch(Exception ex) when(ex is not OperationCanceledException and not OutOfMemoryException){unknown++;}
                if(files.Count%128==0)status?.Invoke($"Reading USB library · {files.Count} files");
            }
        }
        List<Family> games=[];
        foreach(var group in members.GroupBy(m=>m.Metadata.Family)){
            var items=group.OrderBy(m=>m.Metadata.Game.Id).ThenByDescending(m=>m.Metadata.Game.Version).ThenBy(m=>m.MetaId).Select(m=>{
                var required=m.Metadata.InstalledContent.ToList();
                var issues=new List<string>();
                foreach(var r in required){
                    if(!files.TryGetValue(r.Id,out long size))issues.Add($"Missing file {r.Id}.nca");
                    else if(size!=r.Size)issues.Add($"Size mismatch for {r.Id}.nca ({size:N0} of {r.Size:N0} bytes)");
                }
                var version=required.Where(r=>r.Type==3).Select(r=>controls.GetValueOrDefault(r.Id)?.DisplayVersion).FirstOrDefault(v=>v!=null);
                return m with {DisplayVersion=version,Issues=issues,Bytes=AllIds(m).Sum(id=>files.GetValueOrDefault(id))};
            }).ToList();
            var ids=items.SelectMany(AllIds).Distinct().ToArray();
            var artwork=items.Where(m=>m.Metadata.Type is 0x80 or 0x81)
                .OrderByDescending(m=>m.Metadata.Type==0x81).ThenByDescending(m=>m.Metadata.Game.Version)
                .SelectMany(m=>m.Metadata.InstalledContent).Where(r=>r.Type==3)
                .Select(r=>controls.GetValueOrDefault(r.Id)).OfType<ControlInfo>().ToList();
            var name=artwork.Select(c=>c.Name).FirstOrDefault(n=>n!=null);
            if(name==null)foreach(var m in items.OrderBy(m=>m.Metadata.Type!=0x80)){
                var hint=Installer.LocalPath(root,"nexus/pc-import/v1/names/"+m.MetaId+".name");
                if(!File.Exists(hint)||new FileInfo(hint).Length is <=0 or >1024)continue;
                var label=File.ReadAllText(hint);
                if(!string.IsNullOrWhiteSpace(label)&&!label.Any(char.IsControl)){name=label;break;}
            }
            var complete=items.All(m=>m.Issues.Count==0);
            var bases=items.Where(m=>m.Metadata.Type==0x80).SelectMany(m=>m.Metadata.InstalledContent).ToList();
            var baseControls=bases.Where(r=>r.Type==3).Select(r=>controls.GetValueOrDefault(r.Id)).ToArray();
            var marks=baseControls.Select(c=>c?.Mark).OfType<(ulong Title,bool Nexus)>().Where(m=>m.Title==0||m.Title==group.Key).ToList();
            bool marked=marks.Count>0;
            bool separateUpdate=items.Any(m=>m.Metadata.Type==0x81);
            bool compressedBase=bases.Any(r=>compressedContent.Contains(r.Id));
            var state=marks.Any(m=>m.Nexus)&&compressedBase ? (separateUpdate?CompressionState.NexusBase:CompressionState.NexusCompressed)
                : marked&&compressedBase ? (separateUpdate?CompressionState.CompressedBase:CompressionState.Compressed)
                : marked ? CompressionState.Rebuilt : ids.Any(compressedContent.Contains) ? CompressionState.CompressedData
                : complete&&baseControls.Any(c=>c!=null) ? CompressionState.NoMarker : CompressionState.Unknown;
            games.Add(new Family(group.Key,name??$"Game {group.Key:X16}",items,ids.Sum(id=>files.GetValueOrDefault(id)),complete)
                {Compression=state,Icon=artwork.Select(c=>c.Icon).FirstOrDefault(icon=>icon!=null)});
        }
        if(readControls)CompressedReplacement.IdentifyPreviousCopies(root,games,token);
        return new(games.OrderBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase).ToList(),unknown,files);
    }

    // Little-endian, versioned receipt. Paths and commands are never accepted from the drive.
    internal static byte[] Receipt(Family game)
    {
        using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes);
        writer.Write("NPD1"u8);writer.Write(game.Id);writer.Write(game.Members.Count);
        foreach(var m in game.Members){
            var info=m.Metadata;writer.Write(info.Game.Id);writer.Write(info.Game.Version);writer.Write(info.Type);
            var installed=info.InstalledContent.ToList();
            writer.Write(Convert.FromHexString(m.MetaId));writer.Write(installed.Count);
            foreach(var c in installed)writer.Write(Convert.FromHexString(c.Id));
        }
        var data=bytes.ToArray();Need(data.Length<=4*1024*1024-32 && game.Members.Count<=4096,"Too many records in this game.");
        return data.Concat(SHA256.HashData(data)).ToArray();
    }

    static string SyncPath(string root,string name)=>Installer.LocalPath(root,Sync+"/"+name);
    internal static FileStream Lock(string root){
        var path=Installer.LocalPath(root,"nexus/pc-import/v1/session.lock");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    static void Recover(string root,Action check){
        var dir=SyncPath(root,"");System.IO.Directory.CreateDirectory(dir);
        foreach(var folder in System.IO.Directory.EnumerateDirectories(dir)){
            var tx=Path.GetFileName(folder);if(!Guid.TryParseExact(tx,"N",out _))continue;
            SyncPath(root,tx);bool committed=File.Exists(SyncPath(root,tx+".del"));
            foreach(var item in System.IO.Directory.EnumerateFiles(folder,"*.held")){
                var id=Path.GetFileNameWithoutExtension(item);Need(Hex(id,32),"Unexpected staged USB content.");
                var held=SyncPath(root,tx+"/"+id+".held");check();
                if(committed){File.Delete(held);continue;}
                var original=ContentPath(root,id);Need(!File.Exists(original),"Recovery stopped because the original path is already occupied.");
                using(var input=File.OpenRead(held))Need(Packages.Hex(SHA256.HashData(input)[..16])==id,"Recovery found changed staged content.");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Move(held,original,false);
            }
            check();File.Delete(SyncPath(root,tx+"/plan.tmp"));File.Delete(SyncPath(root,tx+"/rights.tmp"));
            if(!committed)File.Delete(SyncPath(root,tx+".rights"));
            System.IO.Directory.Delete(folder,false);
        }
    }
    public static bool NeedsRecovery(string root){
        var path=SyncPath(root,"");return System.IO.Directory.Exists(path)&&System.IO.Directory.EnumerateDirectories(path).Any(p=>Guid.TryParseExact(Path.GetFileName(p),"N",out _));
    }
    public static void RecoverInstallIfNeeded(string root,Action check){
        check();if(!InstallStaging.Pending(root)&&!CompressedReplacement.Pending(root))return;
        using var guard=Lock(root);CompressedReplacement.Recover(root,check);new InstallStaging(root,check).Recover();
    }
    public static void RecoverInterrupted(string root,Action check){check();using var guard=Lock(root);Recover(root,check);}

    public static DeleteResult Delete(string root,KeySet keys,Family selected,CancellationToken token,Action check)
    {
        check();using var guard=Lock(root);CompressedReplacement.Recover(root,check);Recover(root,check);
        var now=Scan(root,keys,token,readControls:false);
        Need(now.Unidentified==0,"Some USB files could not be identified. Deletion is blocked until their ownership can be checked.");
        var game=now.Games.SingleOrDefault(g=>g.Id==selected.Id);
        Need(game!=null && (game.Id>>56)==1 && game.Complete && Receipt(game).SequenceEqual(Receipt(selected)),"This game changed or is outside the supported deletion format. Refresh the library before deleting.");
        var protectedIds=now.Games.Where(g=>g.Id!=selected.Id).SelectMany(g=>g.Members).SelectMany(AllIds).ToHashSet();
        var all=game!.Members.SelectMany(AllIds).Distinct().Where(now.Files.ContainsKey).ToArray();
        var remove=all.Where(id=>!protectedIds.Contains(id)).ToArray();
        var tx=Guid.NewGuid().ToString("N");check();System.IO.Directory.CreateDirectory(SyncPath(root,tx));
        var plan=SyncPath(root,tx+"/plan.tmp");
        try{
            var receipt=Receipt(game);
            using(var output=new FileStream(plan,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(receipt);output.Flush(true);}
            var rights=new SortedSet<string>(StringComparer.Ordinal);
            foreach(var id in remove){
                token.ThrowIfCancellationRequested();check();
                using var input=File.OpenRead(ContentPath(root,id));using var storage=new StreamStorage(input,true);
                var header=new Nca(keys,storage).Header;
                if(header.HasRightsId)rights.Add(Packages.Hex(header.RightsId));
            }
            if(rights.Count>4096)throw new InvalidDataException("Too many permissions in this game.");
            using(var data=new MemoryStream()){
                using(var writer=new BinaryWriter(data,Encoding.UTF8,true)){
                    writer.Write("NPR1"u8);writer.Write(receipt.AsSpan(receipt.Length-32));writer.Write(rights.Count);
                    foreach(var id in rights)writer.Write(Convert.FromHexString(id));
                }
                var body=data.ToArray();
                using var output=new FileStream(SyncPath(root,tx+"/rights.tmp"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                output.Write(body);output.Write(SHA256.HashData(body));output.Flush(true);
            }
            foreach(var id in remove){
                token.ThrowIfCancellationRequested();check();var original=ContentPath(root,id);
                Need(new FileInfo(original).Length==now.Files[id],"A USB content file changed.");
                File.Move(original,SyncPath(root,tx+"/"+id+".held"),false);
            }
            token.ThrowIfCancellationRequested();check();
            File.Move(SyncPath(root,tx+"/rights.tmp"),SyncPath(root,tx+".rights"),false);
            File.Move(plan,SyncPath(root,tx+".del"),false);
        }finally{check();Recover(root,check);}
        return new(remove.Length,remove.Sum(id=>now.Files[id]),all.Length-remove.Length,tx);
    }
}
