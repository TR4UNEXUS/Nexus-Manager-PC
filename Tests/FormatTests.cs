using System.Security.Cryptography;
using System.Text;
using LibHac.Common.Keys;
using LibHac.Common;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Nexus.Pc;
using ZstdSharp;

internal static class FormatTests
{
    static void Check(bool ok){if(!ok)throw new Exception("Package format or staging test failed.");}
    static byte[] Container(bool hfs,List<(string name,byte[] data)> files){
        using var table=new MemoryStream();using var names=new MemoryStream();using var entries=new BinaryWriter(table);
        long off=0;
        foreach(var f in files){
            entries.Write(off);entries.Write((long)f.data.Length);entries.Write((int)names.Length);entries.Write(0);
            if(hfs)entries.Write(new byte[40]);
            names.Write(Encoding.UTF8.GetBytes(f.name));names.WriteByte(0);off+=f.data.Length;
        }
        using var package=new MemoryStream();using var writer=new BinaryWriter(package);
        writer.Write(Encoding.ASCII.GetBytes(hfs?"HFS0":"PFS0"));writer.Write(files.Count);writer.Write((int)names.Length);writer.Write(0);
        writer.Write(table.ToArray());writer.Write(names.ToArray());foreach(var f in files)writer.Write(f.data);
        return package.ToArray();
    }
    static byte[] CompressNca(byte[] raw,bool block){
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream);using var z=new Compressor(1);
        w.Write(raw,0,0x4000);w.Write("NCZSECTN"u8);w.Write(1L);w.Write(0x4000L);w.Write((long)raw.Length-0x4000);
        var key=RandomNumberGenerator.GetBytes(16);byte[] nonce=RandomNumberGenerator.GetBytes(16);
        w.Write(block?1L:3L);w.Write(0L);w.Write(key);w.Write(nonce);
        using var aes=Aes.Create();aes.Key=key;
        var data=raw[0x4000..];byte[] counter=nonce.ToArray();
        for(int p=0;!block && p<data.Length;p+=16){
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counter.AsSpan(8),(ulong)((p+0x4000)/16));
            var mask=aes.EncryptEcb(counter,PaddingMode.None);
            for(int i=0;i<16&&i+p<data.Length;i++)data[p+i]^=mask[i];
        }
        if(!block)w.Write(z.Wrap(data).ToArray());
        else{
            var chunks=data.Chunk(1<<15).Select((b,i)=>{
                var compressed=z.Wrap(b).ToArray();
                return i%2!=0&&compressed.Length<b.Length?compressed:b;
            }).ToArray();
            w.Write("NCZBLOCK"u8);w.Write(new byte[]{2,1,0,15});w.Write(chunks.Length);w.Write((long)data.Length);
            foreach(var b in chunks)w.Write(b.Length);foreach(var b in chunks)w.Write(b);
        }
        return stream.ToArray();
    }
    public static void Run(string root,string path,KeySet keys){
        using var source=File.OpenRead(path);var parts=Packages.Directory(source);
        var files=parts.Select(p=>{source.Position=p.Offset;var b=new byte[(int)p.Size];source.ReadExactly(b);return (name:p.Name,data:b);}).ToList();
        var program=files.First(f=>f.name.EndsWith(".nca")&&!f.name.EndsWith(".cnmt.nca"));
        var original=Packages.Inspect(path,keys,default);var good=original.Files.Select(f=>f.Id).Order().ToArray();
        var secure=Container(true,files);var hfs=Container(true,[("secure",secure)]);
        var xci=new byte[0xf000+hfs.Length];"HEAD"u8.CopyTo(xci.AsSpan(0x100));
        BitConverter.GetBytes(0xf000L).CopyTo(xci,0x130);BitConverter.GetBytes((long)hfs.Length-secure.Length).CopyTo(xci,0x138);hfs.CopyTo(xci,0xf000);
        byte[] XciWithoutCertificate(List<(string name,byte[] data)> input){
            var securePart=Container(true,input);var rootTable=Container(true,[("secure",securePart)]);
            var image=new byte[0xf000+rootTable.Length];Array.Fill<byte>(image,0xff,0x7000,0x200);
            "HEAD"u8.CopyTo(image.AsSpan(0x100));BitConverter.GetBytes(0xf000L).CopyTo(image,0x130);
            BitConverter.GetBytes((long)rootTable.Length-securePart.Length).CopyTo(image,0x138);
            rootTable.CopyTo(image,0xf000);return image;
        }
        void Verify(string name,byte[] image,bool removed=false){
            var file=Path.Combine(root,name);File.WriteAllBytes(file,image);
            var parsed=Packages.Inspect(file,keys,default);Check(parsed.Files.Select(f=>f.Id).Order().SequenceEqual(good));
            var drive=Directory.CreateDirectory(Path.Combine(root,name+"-usb")).FullName;
            var result=Installer.Write(drive,[parsed],new Watch(),default);
            Check(result.Written==good.Length && parsed.Games.SequenceEqual(original.Games) && result.Tickets==(removed?0:original.Tickets.Count) && result.Missing==(removed?original.Files.Select(f=>f.Rights).Where(id=>id.Length>0).Distinct().Count():original.MissingTickets.Length));
            foreach(var f in original.Files){source.Position=f.Part.Offset;byte[] expected=new byte[(int)f.Part.Size];source.ReadExactly(expected);
                string published=Path.Combine(drive,Installer.RelativeContentPath(f.Id));
                Check(File.ReadAllBytes(published).SequenceEqual(expected));
                // NxFileViewer checks the header signature and hashes the NCA bytes.
                // Only synthetic artifacts are used here: deliberately invalid signature,
                // correct CNMT digest (or content ID digest prefix for the metadata itself).
                using var bytes=File.OpenRead(published);
                using var storage=new StreamStorage(bytes,true);
                var nca=new Nca(keys,storage);
                Check(nca.VerifyHeaderSignature()==Validity.Invalid);
                bytes.Position=0;var actual=SHA256.HashData(bytes);
                Check(f.ExpectedHash==null?Packages.Hex(actual[..16])==f.Id:actual.SequenceEqual(f.ExpectedHash));
            }
        }
        Verify("cart.xci",xci);
        var stripped=files.Where(f=>!f.name.EndsWith(".cert")).ToList();
        Verify("no-cert.nsp",Container(false,stripped),true);
        Verify("redacted.xci",XciWithoutCertificate(stripped),true);
        Verify("wiped-cert.nsp",Container(false,files.Select(f=>f.name.EndsWith(".cert")?(f.name,new byte[512]):f).ToList()),true);
        if(program.data.Length>0x4000)foreach(bool block in new[]{false,true}){
            var variant=files.Select(f=>f.name==program.name?(f.name[..^1]+"z",CompressNca(f.data,block)):f).ToList();
            Verify(block?"blocks.nsz":"solid.nsz",Container(false,variant));
            Verify(block?"stripped-blocks.nsz":"stripped-solid.nsz",Container(false,variant.Where(f=>!f.Item1.EndsWith(".cert")).ToList()),true);
        }
        var target=Directory.CreateDirectory(Path.Combine(root,"failed-usb")).FullName;
        var bad=original with {Files=original.Files.Select(f=>f.IsMeta?f with {ExpectedHash=new byte[32]}:f).ToList()};
        try{Installer.Write(target,[bad],new Watch(),default);throw new Exception("Corrupt fixture accepted.");}catch(InvalidDataException){}
        Check(!Directory.EnumerateFiles(target,"*.nca",SearchOption.AllDirectories).Any()&&!Directory.EnumerateFiles(target,"*.bin",SearchOption.AllDirectories).Any());
        Check(!InstallStaging.Pending(target));
        var staging=new InstallStaging(target,()=>{});staging.Begin();File.WriteAllBytes(staging.Temp,new byte[10000]);
        Library.RecoverInstallIfNeeded(target,()=>{});Check(!InstallStaging.Pending(target));
        // Simulate loss of power immediately after staging was renamed for commit.
        staging.Begin();
        var pick=original.Files.First();var recoveredBytes=new byte[(int)pick.Part.Size];
        source.Position=pick.Part.Offset;source.ReadExactly(recoveredBytes);
        var digest=SHA256.HashData(recoveredBytes);
        File.WriteAllBytes(staging.Temp,recoveredBytes);staging.Keep('n',pick.Id,digest);
        string stage=Path.Combine(target,"nexus/pc-import/v1/install-stage"),ready=Path.Combine(target,"nexus/pc-import/v1/install-ready");
        File.WriteAllText(Path.Combine(stage,"plan"),"n-"+pick.Id+"-"+Convert.ToHexString(digest).ToLowerInvariant());Directory.Move(stage,ready);
        Library.RecoverInstallIfNeeded(target,()=>{});
        Check(File.ReadAllBytes(Path.Combine(target,Installer.RelativeContentPath(pick.Id))).SequenceEqual(recoveredBytes)&&!InstallStaging.Pending(target));
        var previous=Installer.Write(target,[original],new Watch(),default);
        Check(previous.Written==good.Length-1 && previous.Reused==1);
        try{Installer.Write(target,[bad],new Watch(),default);throw new Exception("Corrupt expectation accepted.");}catch(InvalidDataException){}
        Check(good.All(id=>File.Exists(Path.Combine(target,Installer.RelativeContentPath(id))))&&!InstallStaging.Pending(target));
        Console.WriteLine("NxFileViewer Modified predicate passed for the installed synthetic NCAs (invalid header signature, matching hash).");
        Console.WriteLine("Container import, byte-for-byte verification, failed-package cleanup, interrupted staging and existing-file preservation passed.");
    }
}
