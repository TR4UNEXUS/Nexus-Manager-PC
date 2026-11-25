using System.Buffers.Binary;
using System.Security.Cryptography;
using Nexus.Pc;

static void Assert(bool value,string message) { if (!value) throw new Exception(message); }
static void Reject(Action action) {
    try { action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { return; }
    throw new Exception("Expected rejection");
}
string root=Path.Combine(Path.GetTempPath(),"nexus-pc-test-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    if(args is ["compression",var keyFile,var sourceFile,var rebuiltFile]){
        var keys=Packages.OpenKeys(keyFile);
        var source=Packages.Inspect(sourceFile,keys,default);
        var rebuilt=Packages.Inspect(rebuiltFile,keys,default);
        Compression.VerifyInput(source,default);Compression.VerifyInput(rebuilt,default);
        Compression.VerifyOutput([source],[rebuilt]);
        Reject(()=>Compression.VerifyOutput([source],[source]));
        Reject(()=>Compression.VerifyOutput([source],[]));
        var before=Directory.CreateDirectory(Path.Combine(root,"before")).FullName;
        Installer.Write(before,[source],new Watch(),default);
        var game=Library.Scan(before,keys,default).Games.Single();
        var snapshot=Path.Combine(root,"snapshot.nsp");
        Compression.Snapshot(before,game,keys,snapshot,default,()=>{});
        var readback=Packages.Inspect(snapshot,keys,default);
        Assert(readback.Files.Select(c=>c.Id).Order().SequenceEqual(source.Files.Select(c=>c.Id).Order()),"Installed snapshot identity");
        var after=Directory.CreateDirectory(Path.Combine(root,"after")).FullName;
        Installer.Write(after,[rebuilt],new Watch(),default);
        var compressed=Library.Scan(after,keys,default).Games.Single();
        Assert(game.Compression==CompressionState.NoMarker,"Original fixture incorrectly marked compressed");
        Assert(compressed.Compression is CompressionState.Compressed or CompressionState.NexusCompressed,"Missing compressed game status");
        Assert(compressed.Complete&&compressed.Id==game.Id&&compressed.Bytes<game.Bytes,"Installed compressed game");
        var scratch=Path.Combine(before,"nexus","pc-import","v1");
        var tickets=Path.Combine(scratch,"tickets");
        if(source.Tickets.Count==1){
            var permission=source.Tickets.Single();var originalFile=Path.Combine(tickets,permission.Id+".bin");
            byte[] archive=File.ReadAllBytes(originalFile);
            "NXT1"u8.CopyTo(archive.AsSpan());Array.Fill<byte>(archive,0x24,32,32);
            var console=Convert.ToHexString(archive.AsSpan(32,32)).ToLowerInvariant();
            var owner=Directory.CreateDirectory(Path.Combine(before,"nexus","tickets","v1",console)).FullName;
            var archiveFile=Path.Combine(owner,permission.Id+".bin");
            SHA256.HashData(archive[..64].Concat(archive[96..]).ToArray()).CopyTo(archive,64);
            Assert(!Compression.ReadPreviewEntries(before,game,keys,default,()=>{}).BaseOnly,"Full preview with all permissions");
            File.Delete(originalFile);
            var baseMember=game.Members.Single(m=>m.Metadata.Type==0x80);
            bool protectedBase=source.Files.Any(c=>c.Rights==permission.Id&&(c.Id==baseMember.MetaId||baseMember.Metadata.InstalledContent.Any(part=>part.Id==c.Id)));
            if(game.Members.Count>1&&!protectedBase){
                var partial=Compression.ReadPreviewEntries(before,game,keys,default,()=>{});
                Assert(partial.BaseOnly&&partial.Entries.Count(e=>e.Name.EndsWith(".cnmt.nca"))==1&&
                    partial.Entries.Any(e=>e.Name==baseMember.MetaId+".cnmt.nca"),"Missing add-on permission must produce only a base estimate");
            }else Reject(()=>Compression.ReadPreviewEntries(before,game,keys,default,()=>{}));
            Reject(()=>Compression.Snapshot(before,game,keys,Path.Combine(root,"incomplete.nsp"),default,()=>{}));
            File.WriteAllBytes(archiveFile,archive);
            var snap2=Path.Combine(root,"switch-backup.nsp");
            Compression.Snapshot(before,game,keys,snap2,default,()=>{});
            Assert(Packages.Inspect(snap2,keys,default).Tickets.Count==1,"Switch common ticket archive lookup");
            archive[64]^=1;File.WriteAllBytes(archiveFile,archive);
            Reject(()=>Compression.Snapshot(before,game,keys,Path.Combine(root,"bad.nsp"),default,()=>{}));
            archive[64]^=1;File.WriteAllBytes(archiveFile,archive);
        }
        var stage=Path.Combine(scratch,"replace-stage");var ready=Path.Combine(scratch,"replace-ready");
        bool interrupted=false;
        Reject(()=>CompressedReplacement.Install(before,game,keys,rebuilt,new Watch(),default,()=>{
            var held=Path.Combine(stage,"held");
            if(!interrupted&&Directory.Exists(held)&&Directory.EnumerateFiles(held).Any()){
                interrupted=true;throw new OperationCanceledException();
            }
        }));
        Assert(interrupted&&!Directory.Exists(stage)&&Library.Scan(before,keys,default).Games.Single().Bytes==game.Bytes,"Cancelled replacement restores original files");
        interrupted=false;
        Reject(()=>CompressedReplacement.Install(before,game,keys,rebuilt,new Watch(),default,()=>{
            if(!interrupted&&Directory.Exists(ready)){interrupted=true;throw new IOException("Simulated drive disconnection");}
        }));
        Assert(interrupted&&Directory.Exists(ready),"Disconnected replacement retains verified commit");
        Library.RecoverInstallIfNeeded(before,()=>{});
        var finished=Library.Scan(before,keys,default).Games.Single();
        Assert(!Directory.EnumerateDirectories(scratch,"replace-*").Any()&&finished.Complete&&finished.Bytes==compressed.Bytes,"Recovered compressed installation");
        Assert(finished.Compression==CompressionState.NexusCompressed,"Exact PC replacement receipt identifies compressed game");
        var swaps=Directory.GetFiles(Path.Combine(scratch,"removals"));
        Assert(swaps.Length==1&&Path.GetExtension(swaps[0])==".swap","Only a replacement receipt is emitted");
        var raw=File.ReadAllBytes(swaps[0]);
        if(Environment.GetEnvironmentVariable("NEXUS_TEST_REPLACEMENT") is {Length:>0} fixtureOut)File.Copy(swaps[0],fixtureOut,true);
        Assert(raw.AsSpan(0,4).SequenceEqual("NXS1"u8)&&SHA256.HashData(raw[..^32]).SequenceEqual(raw[^32..]),"Replacement receipt checksum");
        Assert(!Directory.EnumerateFiles(before,"*.nsp",SearchOption.AllDirectories).Any(),"No stored NSP on USB");
        if(source.Tickets.Count==1)Assert(!File.Exists(Path.Combine(tickets,source.Tickets.Single().Id+".bin")),"Console-bound backup must not become portable");
        Console.WriteLine($"Passed install, installed-game export, content verification and library readback: {game.Bytes:N0} to {compressed.Bytes:N0} bytes.");
        return;
    }
    var packages=Directory.CreateDirectory(Path.Combine(root,"drop")).FullName;
    var nested=Directory.CreateDirectory(Path.Combine(packages,"nested")).FullName;
    foreach(var dropped in new[]{"base.NSP","nested/update.nsz","nested/cart.XcI","notes.txt"})
        File.WriteAllText(Path.Combine(packages,dropped),"test");
    var discovered=PackageDiscovery.Scan([packages,nested,Path.Combine(packages,"base.NSP"),Path.Combine(root,"missing")],[Path.Combine(nested,"update.nsz")]);
    Assert(discovered.Files.Select(f=>Path.GetFileName(f.Path)).SequenceEqual(["base.NSP","cart.XcI"]) &&
           discovered.Files.All(f=>f.Size==4) && discovered.Unreadable==1 && discovered.AlreadyQueued==2,"Recursive mixed folder drop");
    var repeated=PackageDiscovery.Scan([packages],discovered.Files.Select(f=>f.Path).Append(Path.Combine(nested,"update.nsz")));
    Assert(repeated.Files.Count==0 && repeated.AlreadyQueued==3,"Repeated folder drop");
    Reject(()=>PackageDiscovery.Scan([packages],[],new CancellationToken(true)));
    var drive=Directory.CreateDirectory(Path.Combine(root,"volume")).FullName;
    byte[] bytes=new byte[2*1024*1024];RandomNumberGenerator.Fill(bytes);
    string path=Path.Combine(root,"fixture");File.WriteAllBytes(path,bytes);
    byte[] hash=SHA256.HashData(bytes);string id=Convert.ToHexString(hash[..16]).ToLowerInvariant();
    var file=new Content(new("fixture.nca",0,bytes.Length),id,false,hash,"");
    var pkg=new Package(path,[],[file],[],[]);
    var progress=new Watch();
    using var stop=new CancellationTokenSource();
    Reject(()=>Installer.Write(drive,[pkg],new Watch(a=>{if(a.Stage=="Verifying USB copy")stop.Cancel();}),stop.Token));
    var target=Path.Combine(drive,Installer.RelativeContentPath(id));
    Assert(!File.Exists(target)&&!Directory.EnumerateFiles(drive,"*.partial",SearchOption.AllDirectories).Any(),"Cancelled write published content");
    Assert(Installer.Write(drive,[pkg],progress,default).Written==1,"First transfer");
    Assert(Installer.Write(drive,[pkg],new Watch(),default).Reused==1,"Verified reuse");
    Assert(File.ReadAllBytes(target).SequenceEqual(bytes),"Destination mismatch");
    File.WriteAllBytes(target,new byte[bytes.Length]);
    Reject(()=>Installer.Write(drive,[pkg],new Watch(),default));
    Assert(File.ReadAllBytes(target).All(b=>b==0),"Existing destination changed");
    File.Delete(target); File.WriteAllBytes(path,new byte[bytes.Length]);
    Reject(()=>Installer.Write(drive,[pkg],new Watch(),default));
    Assert(!File.Exists(target),"Corrupt source published");
    byte[] rights=Convert.FromHexString("01001000100010000000000000000001");
    var rid=Convert.ToHexString(rights).ToLowerInvariant();
    byte[] tik=new byte[0x2c0], cert=new byte[64];
    BinaryPrimitives.WriteUInt32LittleEndian(tik,0x10004);
    tik[0x280]=2; rights.CopyTo(tik,0x2a0);
    using var input=new MemoryStream(tik.Concat(cert).ToArray());
    var handoff=Packages.Ticket(new("t",0,tik.Length),new("c",tik.Length,cert.Length),input,rid);
    Assert(handoff.AsSpan(0,4).SequenceEqual("NCP1"u8) && handoff.AsSpan(16,16).SequenceEqual(rights),"Handoff header");
    Assert(handoff.AsSpan(96).SequenceEqual(input.ToArray()) && handoff.AsSpan(64,32).SequenceEqual(SHA256.HashData(handoff[..64].Concat(handoff[96..]).ToArray())),"Handoff integrity");
    Reject(()=>Packages.Ticket(new("t",0,tik.Length),new("c",tik.Length,cert.Length),input,new string('0',32)));
    Console.WriteLine("Passed interruption, source/destination corruption, file reuse and ticket framing tests.");
    if(args.Length>=2) {
        // Keep unrelated fault-injection content outside the fixture library.
        drive=Directory.CreateDirectory(Path.Combine(root,"fixture-volume")).FullName;
        var package=Packages.Inspect(args[1],Packages.OpenKeys(args[0]),default);
        InstallQueueTests.Run(root,args[1],Packages.OpenKeys(args[0])).GetAwaiter().GetResult();
        FormatTests.Run(root,args[1],Packages.OpenKeys(args[0]));
        foreach(var name in new[]{"ticketless.nsp","cartridge-media.nsp"}){
            var example=Path.Combine(Path.GetDirectoryName(args[1])!,name);
            if(File.Exists(example)){
                var sampleRoot=Directory.CreateDirectory(Path.Combine(root,"test-"+name)).FullName;
                FormatTests.Run(sampleRoot,example,Packages.OpenKeys(args[0]));
            }
        }
        var first=Installer.Write(drive,[package],new Watch(),default);
        var again=Installer.Write(drive,[package],new Watch(),default);
        Assert(first.Written==package.Files.Count && again.Reused==first.Written && package.Games.Count>0,"Real NSP handoff");
        foreach(var c in package.Files) {
            var actual=SHA256.HashData(File.ReadAllBytes(Path.Combine(drive,Installer.RelativeContentPath(c.Id))));
            Assert(Convert.ToHexString(actual[..16]).Equals(c.Id,StringComparison.OrdinalIgnoreCase),"Independent hash");
        }
        Console.WriteLine($"NSP passed inspection, write, readback and repeat. {package.Games.Count} title(s), {first.Written} NCAs, {first.Tickets} ticket pairs.");
        if(args.Length>=3){
            var keys=Packages.OpenKeys(args[0]);
            var shared=Packages.Inspect(args[2],keys,default);
            Installer.Write(drive,[shared],new Watch(),default);
            var inventory=Library.Scan(drive,keys,default);Assert(inventory.Games.Count==2 && inventory.Unidentified==0,"Scan existing drive");
            var family=inventory.Games.Single(g=>g.Id==package.Games[0].Id);
            Assert(family.Complete,"Optional patch fragments are not needed on USB");
            var optionalInput=Path.Combine(Path.GetDirectoryName(args[1])!,"with-delta.nsp");
            if(File.Exists(optionalInput)){
                var supplied=Packages.Inspect(optionalInput,keys,default);
                Assert(supplied.Files.Count==package.Files.Count,"Installed files exclude optional delivery content");
                var m=family.Members.Single();var optional=m.Metadata.References.Single(r=>r.Type==6);
                var registered=m.Metadata.InstalledContent.Single();
                Assert(!Convert.ToHexString(Library.Receipt(family)).Contains(optional.Id,StringComparison.OrdinalIgnoreCase),"Receipt agrees with Switch installed-content database");
                var requiredPath=Path.Combine(drive,Installer.RelativeContentPath(registered.Id));
                File.Move(requiredPath,requiredPath+".test");
                var broken=Library.Scan(drive,keys,default).Games.Single(g=>g.Id==family.Id);
                Assert(!broken.Complete && broken.Members.Single().Issues.Single().Contains(registered.Id),"Required missing file reported on the correct component");
                File.WriteAllBytes(requiredPath,new byte[128]);
                broken=Library.Scan(drive,keys,default).Games.Single(g=>g.Id==family.Id);
                Assert(!broken.Complete && broken.Members.Single().Issues.Single().StartsWith("Size mismatch"),"Short required file reported");
                File.Move(requiredPath+".test",requiredPath,true);
            }
            var only=family.Members[0].MetaId;
            var dir=Path.Combine(drive,"nexus","pc-import","v1","removals");
            var tx=Directory.CreateDirectory(Path.Combine(dir,Guid.NewGuid().ToString("N"))).FullName;
            File.Move(Path.Combine(drive,Installer.RelativeContentPath(only)),Path.Combine(tx,only+".held"));
            Assert(Library.NeedsRecovery(drive),"Interrupted stage detected");
            Reject(()=>Installer.Write(drive,[package],new Watch(),default));
            Library.RecoverInterrupted(drive,()=>{});
            Assert(File.Exists(Path.Combine(drive,Installer.RelativeContentPath(only))),"Interrupted stage restored");
            var removed=Library.Delete(drive,keys,family,default,()=>{});
            Assert(removed.Files==1 && removed.Shared==1 && removed.Bytes>0,"Shared content preserved");
            Assert(Library.Scan(drive,keys,default).Games.Single().Complete,"Other game still complete");
            var receipt=File.ReadAllBytes(Path.Combine(dir,removed.Receipt+".del"));
            Assert(receipt.SequenceEqual(Library.Receipt(family)) && !Library.NeedsRecovery(drive),"Committed receipt");
            var ticketIds=File.ReadAllBytes(Path.Combine(dir,removed.Receipt+".rights"));
            Assert(ticketIds.AsSpan(0,4).SequenceEqual("NPR1"u8)&&ticketIds.AsSpan(4,32).SequenceEqual(receipt.AsSpan(receipt.Length-32))&&
                SHA256.HashData(ticketIds[..^32]).SequenceEqual(ticketIds[^32..]),"Bound permission sidecar");
            Assert(!File.Exists(Path.Combine(drive,Installer.RelativeContentPath(only))),"USB bytes actually removed");
            if(args.Length==4){
                Directory.CreateDirectory(args[3]);File.WriteAllBytes(Path.Combine(args[3],"synthetic.del"),receipt);
                File.WriteAllBytes(Path.Combine(args[3],"synthetic.rights"),ticketIds);
                File.WriteAllBytes(Path.Combine(args[3],"synthetic.pair"),package.Tickets.Single().Bundle);
                var standalone=Directory.CreateDirectory(Path.Combine(root,"standalone")).FullName;
                Installer.Write(standalone,[package],new Watch(),default);
                var onlyGame=Library.Scan(standalone,keys,default).Games.Single();
                var deletion=Library.Delete(standalone,keys,onlyGame,default,()=>{});
                var log=Path.Combine(standalone,"nexus","pc-import","v1","removals",deletion.Receipt);
                File.Copy(log+".del",Path.Combine(args[3],"exclusive.del"),true);
                File.Copy(log+".rights",Path.Combine(args[3],"exclusive.rights"),true);
            }
            Installer.Write(drive,[package],new Watch(),default);
            Assert(Library.Scan(drive,keys,default).Games.Count==2,"Reinstall after deletion");
            var extras=Path.Combine(Path.GetDirectoryName(args[1])!,"extras.nsp");
            if(File.Exists(extras)){
                Installer.Write(drive,[Packages.Inspect(extras,keys,default)],new Watch(),default);
                var expanded=Library.Scan(drive,keys,default).Games.Single(g=>g.Id==family.Id);
                Assert(expanded.Complete && expanded.Members.Count==4,"Group the base, update and DLC");
                Assert(expanded.Members.Single(m=>m.Metadata.Type==0x81).Metadata.Game.Version==65536,"Identify installed update version");
                Assert(expanded.Members.Where(m=>m.Metadata.Type==0x82).Select(m=>m.Metadata.Game.Id).Distinct().Count()==2,"List each installed DLC");
                Console.WriteLine("Verified game details for a base, one update and two distinct DLC titles.");
            }
            Console.WriteLine("Passed library discovery, interrupted deletion recovery, permanent USB deletion, receipt, shared ownership and reinstallation.");
        }
    }
} finally {Directory.Delete(root,true);}
class Watch(Action<Activity>? onReport=null):IProgress<Activity> {
    long previous;
    public void Report(Activity value) {
        if(value.Done<previous || value.Done>value.Total)throw new Exception("Nonmonotonic progress");
        previous=value.Done;onReport?.Invoke(value);
    }
}
