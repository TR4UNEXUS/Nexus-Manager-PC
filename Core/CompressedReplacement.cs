using System.Security.Cryptography;
using LibHac.Common.Keys;

namespace Nexus.Pc;

public static class CompressedReplacement
{
    const string Parent="nexus/pc-import/v1/";
    const string Stage=Parent+"replace-stage";
    const string Ready=Parent+"replace-ready";
    const string Done=Parent+"replace-done";
    static bool Id(string value)=>value.Length==32&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static string At(string root,string relative)=>Installer.LocalPath(root,relative);
    static string Content(string root,string id)=>At(root,Installer.RelativeContentPath(id));
    static void Require(bool ok,string reason){if(!ok)throw new InvalidDataException(reason);}

    internal static bool Pending(string root)=>new[]{Stage,Ready,Done}.Any(p=>Directory.Exists(At(root,p)));

    public static long Install(string root,Family selected,KeySet keys,Package output,IProgress<Activity> progress,CancellationToken token,Action check)
    {
        check();using var guard=Library.Lock(root);Recover(root,check);
        if(InstallStaging.Pending(root)||Library.NeedsRecovery(root))throw new IOException("Finish USB library recovery first.");
        var library=Library.Scan(root,keys,token,readControls:false);
        var game=library.Games.SingleOrDefault(g=>g.Id==selected.Id);
        Require(library.Unidentified==0&&game is { Complete:true }&&Library.Receipt(game).SequenceEqual(Library.Receipt(selected)),
            "The selected game or its USB library changed. Refresh it before compressing.");
        string stage=Directory.CreateDirectory(At(root,Stage)).FullName;
        try {
            // The source ticket remains in its own backup. Replacement publishes game content only.
            Installer.Write(stage,[output with {Tickets=[]}],progress,token,check);
            var staged=Library.Scan(stage,keys,token,readControls:false);
            var fresh=staged.Games.Single();
            var expected=Compression.ExpectedTitles(game!.Members.Select(m=>m.Metadata.Game));
            Require(staged.Unidentified==0 && fresh.Complete && fresh.Id==game.Id && fresh.Members.Count==expected.Count &&
                fresh.Members.All(m=>m.Metadata.Type is 0x80 or 0x82 && expected.TryGetValue(m.Metadata.Game.Id,out var v)&&v==m.Metadata.Game.Version) &&
                fresh.Members.Select(m=>m.Metadata.Game.Id).Distinct().Count()==expected.Count,"The compressed package does not match the selected game family.");
            var oldIds=game.Members.SelectMany(m=>m.Metadata.References.Select(r=>r.Id).Prepend(m.MetaId)).Distinct().Where(library.Files.ContainsKey);
            var shared=library.Games.Where(g=>g.Id!=game.Id).SelectMany(g=>g.Members).SelectMany(m=>m.Metadata.References.Select(r=>r.Id).Prepend(m.MetaId)).ToHashSet();
            var obsolete=oldIds.Where(id=>!staged.Files.ContainsKey(id)&&!shared.Contains(id)).ToArray();
            long freed=obsolete.Sum(id=>library.Files[id])-staged.Files.Where(k=>!library.Files.ContainsKey(k.Key)).Sum(k=>k.Value);
            Require(freed>0,"Compression would not free space on this drive. The installed copy was kept.");

            foreach(string id in staged.Files.Keys.Where(library.Files.ContainsKey)){
                token.ThrowIfCancellationRequested();check();VerifyId(Content(root,id),id);
            }
            var original=Library.Receipt(game);var rebuilt=Library.Receipt(fresh);
            using var body=new MemoryStream();using(var w=new BinaryWriter(body,System.Text.Encoding.UTF8,true)){
                w.Write("NXS1"u8);w.Write(original.Length);w.Write(original);w.Write(rebuilt);
            }
            var bytes=body.ToArray();WriteNew(At(root,Stage+"/receipt"),bytes.Concat(SHA256.HashData(bytes)).ToArray());
            Directory.CreateDirectory(At(root,Stage+"/held"));
            foreach(string id in obsolete){
                token.ThrowIfCancellationRequested();check();
                File.Move(Content(root,id),At(root,Stage+"/held/"+id),false);
            }
            token.ThrowIfCancellationRequested();check();
            Directory.Move(stage,At(root,Ready));  // durable commit; recovery can now finish without the PC's NSP
            Finish(root,check);
            return freed;
        }catch{
            check();if(Directory.Exists(At(root,Stage)))Recover(root,check);
            throw;
        }
    }

    static void WriteNew(string path,byte[] data){
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        file.Write(data);file.Flush(true);
    }
    static void VerifyId(string path,string id){
        using var file=File.OpenRead(path);
        Require(Packages.Hex(SHA256.HashData(file).AsSpan(0,16))==id,"A file in the replacement changed. Reconnect the same drive and check the library.");
    }

    // PC 0.1.6 wrote a replacement receipt before the compressor began tagging its output.
    // Use a receipt for display only when it names the exact family currently on the drive.
    internal static void IdentifyPreviousCopies(string root,List<Family> games,CancellationToken token){
        if(!games.Any(g=>g.Compression!=CompressionState.NexusCompressed&&g.Complete))return;
        try{
            var directory=At(root,Parent+"removals");
            if(!Directory.Exists(directory))return;
            var candidates=games.Select((game,index)=>(game,index)).Where(x=>x.game.Complete&&(x.game.Id>>56)==1)
                .ToDictionary(x=>x.game.Id,x=>x.index);
            if(candidates.Count==0)return;
            foreach(var path in Directory.EnumerateFiles(directory,"*.swap")){
                token.ThrowIfCancellationRequested();
                string name=Path.GetFileNameWithoutExtension(path);
                if(!Id(name))continue;
                try{
                    byte[] raw;
                    using(var file=File.OpenRead(At(root,Parent+"removals/"+name+".swap"))){
                        if(file.Length is <136 or >8388648)continue;
                        raw=new byte[(int)file.Length];file.ReadExactly(raw);
                    }
                    if(!raw.AsSpan(0,4).SequenceEqual("NXS1"u8))continue;
                    int old=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
                    if(old is <48 or >4194304 || old>raw.Length-88)continue;
                    var fresh=raw.AsSpan(8+old,raw.Length-40-old);
                    var gameId=System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(fresh[4..]);
                    if(!candidates.TryGetValue(gameId,out int index))continue;
                    var digest=SHA256.HashData(raw.AsSpan(0,raw.Length-32));
                    if(!raw.AsSpan(raw.Length-32).SequenceEqual(digest)||Packages.Hex(digest.AsSpan(0,16))!=name||
                        !fresh.SequenceEqual(Library.Receipt(games[index])))continue;
                    games[index]=games[index] with {Compression=CompressionState.NexusCompressed};
                    candidates.Remove(gameId);if(candidates.Count==0)break;
                }catch(IOException){}catch(UnauthorizedAccessException){}catch(InvalidDataException){}
            }
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }

    internal static void Recover(string root,Action check){
        if(!Pending(root))return;
        check();
        if(Directory.Exists(At(root,Done)))Clear(root,Done,check);
        if(Directory.Exists(At(root,Ready)))Finish(root,check);
        if(!Directory.Exists(At(root,Stage)))return;
        string held=At(root,Stage+"/held");
        if(Directory.Exists(held))foreach(var path in Directory.EnumerateFiles(held)){
            string id=Path.GetFileName(path);Require(Id(id),"Unexpected file in replacement recovery.");
            check();string source=At(root,Stage+"/held/"+id);
            VerifyId(source,id);File.Move(source,Content(root,id),false);
        }
        Clear(root,Stage,check);
    }
    // Derive the file set from the checksummed family receipts, including during recovery.
    static List<string> PublicationOrder(byte[] data){
        int oldLength=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int available=data.Length-8-32;
        Require(oldLength>=48&&oldLength<=4194304&&available-oldLength is >=48 and <=4194304,"Invalid replacement family lengths.");
        static (ulong Game,List<string> Files) ReadFamily(byte[] bytes,int start,int length){
            var payload=bytes.AsSpan(start,length-32);
            Require(payload[..4].SequenceEqual("NPD1"u8)&&SHA256.HashData(payload).AsSpan().SequenceEqual(bytes.AsSpan(start+length-32,32)),"Invalid family checksum.");
            using var stream=new MemoryStream(bytes,start,length-32,false);
            using var reader=new BinaryReader(stream);
            reader.ReadUInt32();ulong game=reader.ReadUInt64();int count=reader.ReadInt32();
            Require(count is >0 and <=4096,"Invalid family member count.");
            var files=new HashSet<string>();var metas=new HashSet<string>();
            string ReadId(){
                var id=reader.ReadBytes(16);Require(id.Length==16&&id.Any(b=>b!=0),"Invalid content ID in family receipt.");return Packages.Hex(id);
            }
            for(int n=0;n<count;n++){
                reader.ReadUInt64();reader.ReadUInt32();reader.ReadByte();
                Require(metas.Add(ReadId()),"Duplicate metadata in family receipt.");
                int refs=reader.ReadInt32();Require(refs is >0 and <=4095,"Invalid family content count.");
                for(int i=0;i<refs;i++)files.Add(ReadId());
            }
            Require(stream.Position==stream.Length,"Invalid end of family receipt.");
            files.ExceptWith(metas);
            return (game,files.Order(StringComparer.Ordinal).Concat(metas.Order(StringComparer.Ordinal)).ToList());
        }
        var old=ReadFamily(data,8,oldLength);
        var fresh=ReadFamily(data,8+oldLength,available-oldLength);
        Require(old.Game==fresh.Game&&old.Game>>56==1,"The replacement belongs to a different game.");
        return fresh.Files;
    }
    static void Finish(string root,Action check){
        string receiptPath=At(root,Ready+"/receipt");
        Require(new FileInfo(receiptPath).Length is >=136 and <=8388656,"Invalid replacement receipt.");
        var receipt=File.ReadAllBytes(receiptPath);var digest=SHA256.HashData(receipt.AsSpan(0,receipt.Length-32));
        Require(receipt.AsSpan(0,4).SequenceEqual("NXS1"u8)&&digest.AsSpan().SequenceEqual(receipt.AsSpan(receipt.Length-32)),"Replacement receipt checksum failed.");
        var ids=PublicationOrder(receipt);
        var ready=At(root,Ready);
        foreach(string id in ids){
            check();var src=Content(ready,id);var dest=Content(root,id);
            if(File.Exists(dest))VerifyId(dest,id);
            else VerifyId(src,id);
        }
        foreach(string id in ids){
            check();var dest=Content(root,id);if(File.Exists(dest))continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Move(Content(ready,id),dest,false);
        }
        string sync=At(root,Parent+"removals/"+Packages.Hex(digest.AsSpan(0,16))+".swap");
        Directory.CreateDirectory(Path.GetDirectoryName(sync)!);check();
        if(File.Exists(sync))Require(new FileInfo(sync).Length==receipt.Length&&File.ReadAllBytes(sync).SequenceEqual(receipt),"A different replacement receipt already exists.");
        else{
            string temp=At(root,Ready+"/posting");File.Delete(temp);WriteNew(temp,receipt);File.Move(temp,sync,false);
        }
        check();Directory.Move(ready,At(root,Done));Clear(root,Done,check);
    }

    // Remove only these app-owned transaction directories. Validate every child path before traversal.
    static void Clear(string root,string relative,Action check){
        var dir=At(root,relative);
        foreach(string entry in Directory.EnumerateFileSystemEntries(dir)){
            string child=relative+"/"+Path.GetFileName(entry),safe=At(root,child);check();
            if(Directory.Exists(safe))Clear(root,child,check);else File.Delete(safe);
        }
        check();Directory.Delete(dir,false);
    }
}
