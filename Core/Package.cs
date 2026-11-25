using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;

namespace Nexus.Pc;

public record Part(string Name, long Offset, long Size, long StoredSize=0);
public record Content(Part Part, string Id, bool IsMeta, byte[]? ExpectedHash, string Rights);
public record Permission(string Id, byte[] Bundle);
public record Game(ulong Id, uint Version, string Kind)
{
    public ulong FamilyId { get; init; }
}
public record Reference(string Id,long Size,byte[] Hash,byte Type);
public record Manifest(Game Game,ulong Family,byte Type,List<Reference> References)
{
    // Delta fragments are patch-delivery files. Switch Manager leaves them
    // out of registered content, including its HOME reconciliation receipts.
    public IEnumerable<Reference> InstalledContent => References.Where(r=>r.Type!=6);
}
public record Package(string Path, List<Game> Games, List<Content> Files, List<Permission> Tickets, string[] MissingTickets);

public static class Packages
{
    public static bool SupportedFile(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() is ".nsp" or ".nsx" or ".nsz" or ".xci" or ".xcz";
    static ushort U16(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16LittleEndian(b);
    static uint U32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
    static long I64(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt64LittleEndian(b);
    internal static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
    static void Require(bool ok, string reason) { if (!ok) throw new InvalidDataException(reason); }

    public static KeySet OpenKeys(string path)
    {
        var info = new FileInfo(path);
        Require(info.Exists && info.Length is > 0 and <= 4194304, "Select a valid local prod.keys file.");
        return ExternalKeyReader.ReadKeyFile(path, null, null, null);
    }

    internal static List<Part> Directory(Stream input,long start=0,long? partitionSize=null,bool hfs=false)
    {
        input.Position=start;
        long limit=checked(start+(partitionSize??(input.Length-start)));
        Require(start>=0 && limit<=input.Length && limit-start>=16,"Invalid package partition bounds.");
        int rowSize=hfs?64:24;
        byte[] header = new byte[16]; input.ReadExactly(header);
        Require(header.AsSpan(0,4).SequenceEqual(hfs?"HFS0"u8:"PFS0"u8), "Invalid package directory signature.");
        uint count = U32(header.AsSpan(4)), namesSize = U32(header.AsSpan(8));
        long data = checked(start + 16L + count * rowSize + namesSize);
        Require(count is > 0 and <= 8192 && namesSize is > 0 and <= 1048576 && data <= limit, "Invalid package file table.");
        var table = new byte[count * rowSize]; var names = new byte[namesSize];
        input.ReadExactly(table); input.ReadExactly(names);
        List<Part> parts = []; HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        for (int i=0; i<count; i++)
        {
            var entry = table.AsSpan(i*rowSize,rowSize); long offset=I64(entry), size=I64(entry[8..]);
            uint nameOffset=U32(entry[16..]);
            Require(nameOffset<names.Length && offset>=0 && size>=0 && offset<=limit-data && size<=limit-data-offset, "An NSP entry points outside the package.");
            int end=Array.IndexOf(names,(byte)0,(int)nameOffset);
            Require(end>(int)nameOffset && end-nameOffset<=255, "Invalid package entry name.");
            string name=new UTF8Encoding(false,true).GetString(names,(int)nameOffset,end-(int)nameOffset);
            Require(!name.Any(c=>char.IsControl(c)||"/\\:".Contains(c)) && unique.Add(name), "Invalid or duplicate package entry.");
            parts.Add(new(name,data+offset,size));
        }
        long previousEnd=data;
        foreach(var part in parts.Where(p=>p.Size>0).OrderBy(p=>p.Offset))
        {
            Require(part.Offset>=previousEnd,"Overlapping package entries.");
            previousEnd=part.Offset+part.Size;
        }
        return parts;
    }

    internal static List<Part> Cartridge(Stream input)
    {
        long root=0;long headerSize=0;
        foreach(int prefix in new[]{0,0x1000}){
            if(input.Length<prefix+0x140)continue;
            input.Position=prefix+0x100;var head=new byte[64];input.ReadExactly(head);
            if(!head.AsSpan(0,4).SequenceEqual("HEAD"u8))continue;
            long offset=I64(head.AsSpan(0x30));headerSize=I64(head.AsSpan(0x38));
            Require(offset>=0x200 && offset<=input.Length-prefix && headerSize>=16 && headerSize<=input.Length-prefix-offset,"Invalid XCI header.");
            root=prefix+offset;break;
        }
        if(root==0){
            foreach(long location in new long[]{0xf000,0x10000}){
                if(input.Length-location<16)continue;
                input.Position=location;byte[] magic=new byte[4];input.ReadExactly(magic);
                if(magic.AsSpan().SequenceEqual("HFS0"u8)){root=location;break;}
            }
            Require(root!=0,"XCI secure partition table not found.");
        }
        var partitions=Directory(input,root,null,true);
        if(headerSize!=0)Require(input.Position<=root+headerSize,"XCI root table exceeds its declared header.");
        var secure=partitions.Find(p=>p.Name=="secure")??throw new InvalidDataException("The XCI has no secure game partition.");
        return Directory(input,secure.Offset,secure.Size,true);
    }

    static byte[] Read(Stream stream, Part part, int limit)
    {
        Require(part.Size is >0 && part.Size<=limit, "An entry has an unsupported size.");
        stream.Position=part.Offset;
        var bytes=new byte[(int)part.Size]; stream.ReadExactly(bytes);return bytes;
    }

    static bool HasPermissionFile(Stream stream,Part part)
    {
        if(part.Size==0)return false;
        byte[] bytes=Read(stream,part,65536);
        // Some repackers leave an erased placeholder instead of omitting the entry.
        return bytes.Any(b=>b!=0 && b!=0xff);
    }

    internal static byte[] Ticket(Part ticket, Part cert, Stream stream, string id)
    {
        byte[] tik=Read(stream,ticket,65536), chain=Read(stream,cert,65536);
        Require(tik.Length>=4,"Truncated ticket.");
        int offset=TicketBodyOffset(tik);
        Require(offset>=0 && tik.Length>=offset+0x180 && tik[offset+0x140]==2 && tik[offset+0x141]<=1 &&
            Hex(tik.AsSpan(offset+0x160,16))==id,"The ticket does not match the required rights ID.");
        var file=new byte[96+tik.Length+chain.Length];
        "NCP1"u8.CopyTo(file); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4),1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8),tik.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12),chain.Length);
        Convert.FromHexString(id).CopyTo(file,16); tik.CopyTo(file,96);chain.CopyTo(file,96+tik.Length);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(file,0,64);hash.AppendData(file,96,file.Length-96);hash.GetHashAndReset().CopyTo(file,64);
        return file;
    }
    internal static int TicketBodyOffset(ReadOnlySpan<byte> ticket) =>
        U32(ticket) switch {65536 or 65539=>0x240,65537 or 65540=>0x140,65538 or 65541=>0x80,65542=>0x40,_=>-1};

    internal static Manifest ReadManifest(Nca nca,bool homebrewOwnership=false)
    {
        using var section=nca.OpenStorage(0,IntegrityCheckLevel.ErrorOnInvalid);
        using var index=section.AsStream(FileAccess.Read,true);
        var matches=Directory(index).Where(p=>p.Name.EndsWith(".cnmt",StringComparison.OrdinalIgnoreCase)).ToList();
        Require(matches.Count==1,"Expected one content manifest inside each metadata NCA.");
        var b=Read(index,matches[0],4*1024*1024);
        Require(b.Length>=32,"Truncated content manifest.");
        var title=BinaryPrimitives.ReadUInt64LittleEndian(b);
        var kind=b[12] switch{0x80=>"Base game",0x81=>"Update",0x82=>"DLC",0x84=>"Data patch",_=>""};
        Require(title==nca.Header.TitleId,"The game ID in the metadata differs from the NCA header.");
        Require(((title>>56)==1 || (homebrewOwnership && (title>>56)==5)) && kind!="","Switch USB import supports game, update and DLC title IDs. This package has ID "+title.ToString("X16")+".");
        Require(b[13]==0 && b[22]==0,"This manifest does not describe a complete Switch installation.");

        int ext=U16(b.AsSpan(14)), count=U16(b.AsSpan(16)), metaCount=U16(b.AsSpan(18));
        int minExt=b[12] switch {0x81=>24,0x84=>32,_=>16};
        int first=32+ext, end=first+count*56;
        Require(ext>=minExt && ext<=4096 && count is >0 and <=4095 && metaCount<=4096 && end<=b.Length,"Invalid content manifest references.");
        long tail=metaCount*16L;
        if(b[12]==0x81)tail+=U32(b.AsSpan(44));
        if(b[12]==0x84)tail+=U32(b.AsSpan(52));
        Require((b.Length-end-tail) is 0 or 32 || homebrewOwnership,"The manifest has an incomplete or unexpected metadata tail.");
        if(b[12]!=0x80)Require((BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b[12]==0x84?40:32))>>56)==1,"The manifest has no valid parent game.");
        HashSet<string> seen=[];
        List<Reference> refs=[];
        for(int i=0;i<count;i++)
        {
            var c=b.AsSpan(first+i*56,56);var contentId=Hex(c.Slice(32,16));
            var hash=c[..32].ToArray();long size=U32(c[48..])|((long)c[52]<<32);
            Require(hash.AsSpan(0,16).SequenceEqual(c.Slice(32,16)) && size>=0xc00 &&
                c[54] is >=1 and <=6 && contentId!=new string('0',32) && seen.Add(contentId),"Invalid or duplicate manifest content reference.");
            refs.Add(new(contentId,size,hash,c[54]));
        }
        ulong family=b[12]==0x80?title:BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(b[12]==0x84?40:32));
        Require(refs.Any(r=>r.Type!=6),"No installable content in the manifest.");
        return new(new Game(title,U32(b.AsSpan(8)),kind){FamilyId=family},family,b[12],refs);
    }

    public static Package Inspect(string path, KeySet keys, CancellationToken cancel)
    {
        using var stream=File.OpenRead(path);
        var parts=System.IO.Path.GetExtension(path).ToLowerInvariant() is ".xci" or ".xcz"?Cartridge(stream):Directory(stream);
        using var storage=new StreamStorage(stream,true);
        Dictionary<string,Content> entries=new(StringComparer.Ordinal);
        Dictionary<string,(long Size,byte[] Hash)> references=[];
        List<Game> games=[];
        foreach(var part in parts)
        {
            cancel.ThrowIfCancellationRequested();
            bool compressed=part.Name.EndsWith(".ncz",StringComparison.OrdinalIgnoreCase);
            if(!compressed && !part.Name.EndsWith(".nca",StringComparison.OrdinalIgnoreCase))continue;
            var id=part.Name.Split('.')[0].ToLowerInvariant();
            Require(id.Length==32 && id.All(Uri.IsHexDigit) && !entries.ContainsKey(id),"Invalid or duplicate NCA ID.");
            using var slice=storage.Slice(part.Offset,part.Size,true);
            var nca=new Nca(keys,slice);var header=nca.Header;
            Require(compressed?header.NcaSize>=0x4000 && part.Size>=0x4010:header.NcaSize==part.Size,"An NCA is truncated or has the wrong size.");
            bool meta=header.ContentType==NcaContentType.Meta;
            var rights=header.HasRightsId?Hex(header.RightsId):"";
            var logical=compressed?part with {Size=header.NcaSize,StoredSize=part.Size}:part;
            entries.Add(id,new(logical,id,meta,null,rights));
            if(!meta)continue;
            Manifest manifest;
            if(compressed){
                Require(logical.Size<=64*1024*1024,"Compressed metadata is too large.");
                using var bytes=new MemoryStream((int)logical.Size);
                Ncz.Decode(stream,logical,(b,n)=>bytes.Write(b,0,n),cancel);
                bytes.Position=0;using var decoded=new StreamStorage(bytes,true);
                manifest=ReadManifest(new Nca(keys,decoded));
            }else manifest=ReadManifest(nca);
            games.Add(manifest.Game);
            foreach(var content in manifest.InstalledContent)
            {
                if(references.TryGetValue(content.Id,out var old))Require(content.Size==old.Size && old.Hash.SequenceEqual(content.Hash),"Conflicting content manifests.");
                references[content.Id]=(content.Size,content.Hash);
            }
        }
        Require(games.Count>0,"No supported game metadata was found.");
        foreach(var (id,item) in references)
        {
            Require(entries.TryGetValue(id,out var content) && !content.IsMeta && content.Part.Size==item.Size,"Missing or wrong-size NCA: "+id);
            entries[id]=content! with {ExpectedHash=item.Hash};
        }
        var files=entries.Values.Where(c=>c.IsMeta||references.ContainsKey(c.Id)).OrderBy(c=>c.IsMeta).ToList();
        List<Permission> tickets=[];List<string> missing=[];
        var byName=parts.ToDictionary(p=>p.Name,StringComparer.OrdinalIgnoreCase);
        foreach(var id in files.Select(c=>c.Rights).Where(s=>s.Length>0).Distinct())
        {
            if(byName.TryGetValue(id+".tik",out var tik) && byName.TryGetValue(id+".cert",out var cert) &&
                HasPermissionFile(stream,tik) && HasPermissionFile(stream,cert))
                tickets.Add(new(id,Ticket(tik,cert,stream,id)));
            else missing.Add(id);
        }
        return new(path,games,files,tickets,missing.ToArray());
    }
}
