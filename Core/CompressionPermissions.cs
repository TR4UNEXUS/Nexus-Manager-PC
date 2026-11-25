using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Nexus.Pc;

internal static class CompressionPermissions
{
    internal static (byte[] Ticket,byte[] Certificate)? Find(string root,string right,CancellationToken token,
        IReadOnlyDictionary<string,Permission>? originals)
    {
        if(originals!=null && originals.TryGetValue(right,out var original)){
            var input=Decode(original.Bundle,right,null);
            if(input.HasValue)return input;
            throw new IOException("This package has a console-specific permission that PC Manager cannot use for compression.");
        }
        string? issue=null;
        (byte[] Ticket,byte[] Certificate)? Candidate(string relative,string? owner){
            try{
                var bytes=Read(Installer.LocalPath(root,relative));
                return bytes==null?null:Decode(bytes,right,owner);
            }catch(InvalidDataException){issue="The stored permission did not pass verification. Select its original package";return null;}
        }
        var portable=Candidate($"nexus/pc-import/v1/tickets/{right}.bin",null);
        if(portable.HasValue)return portable;

        var directory=Installer.LocalPath(root,"nexus/tickets/v1");
        (byte[] Ticket,byte[] Certificate)? common=null;
        if(Directory.Exists(directory))foreach(string folder in Directory.EnumerateDirectories(directory)){
            token.ThrowIfCancellationRequested();
            string console=Path.GetFileName(folder);
            if(console.Length!=64 || !console.All(Uri.IsHexDigit))continue;
            // The same-console backup remains in its original location. Only common
            // tickets can be inputs to this temporary local compression session.
            var candidate=Candidate($"nexus/tickets/v1/{console}/{right}.bin",console);
            if(candidate==null)continue;
            if(common.HasValue && !common.Value.Ticket.SequenceEqual(candidate.Value.Ticket))
                throw new CompressionPermissionNeeded(right,"Conflicting USB permissions. Select the original package for this game");
            common=candidate;
        }
        if(!common.HasValue&&issue!=null)throw new CompressionPermissionNeeded(right,issue);
        return common;
    }

    static byte[]? Read(string path){
        try{
            using var stream=File.OpenRead(path);
            if(stream.Length is <96 or >131168)throw new InvalidDataException("A USB ticket backup has an invalid size.");
            var data=new byte[(int)stream.Length];stream.ReadExactly(data);return data;
        }catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}
    }

    static (byte[] Ticket,byte[] Certificate)? Decode(byte[] b,string right,string? console){
        if(b.Length<96)throw new InvalidDataException("Truncated USB ticket backup.");
        int tik=BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)),cert=BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(b,0,64);hash.AppendData(b,96,b.Length-96);
        if(!b.AsSpan(0,4).SequenceEqual(console==null?"NCP1"u8:"NXT1"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4))!=1 ||
            !b.AsSpan(16,16).SequenceEqual(Convert.FromHexString(right)) ||
            !b.AsSpan(32,32).SequenceEqual(console==null?new byte[32]:Convert.FromHexString(console)) ||
            !b.AsSpan(64,32).SequenceEqual(hash.GetHashAndReset()) ||
            tik is <=0 or >65536 || cert is <=0 or >65536 || 96L+tik+cert!=b.Length)
            throw new InvalidDataException("A USB ticket backup failed its identity or checksum check. Choose the original package.");
        using var payload=new MemoryStream(b,false);
        Packages.Ticket(new(right+".tik",96,tik),new(right+".cert",96+tik,cert),payload,right);
        // Tickets with title-key type 1 belong to an individual console.
        int body=Packages.TicketBodyOffset(b.AsSpan(96,tik));
        return b[96+body+0x141]==0 ? (b[96..(96+tik)],b[(96+tik)..]) : null;
    }
}

public sealed class CompressionPermissionNeeded(string rightsId,string message):IOException(message) { public string RightsId { get; } = rightsId; }

