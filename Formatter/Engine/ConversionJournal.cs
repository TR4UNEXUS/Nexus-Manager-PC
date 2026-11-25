// Durable before/after-image journal for a file-preserving conversion.
// Preparation/validation touches ONLY a new regular PC file. Applying requires
// an already exclusively locked, identity-pinned IBlockDevice supplied by caller.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace NexusFormatter {
 internal sealed class ConversionJournal : IBlockDevice,IDisposable {
  sealed class Record {internal ulong Offset;internal int Length;internal long Before,After;}
  readonly IBlockDevice disk;readonly FileStream file;readonly byte[] binding;
  readonly List<Record> records=new List<Record>(),sorted=new List<Record>();
  bool sealedFile;
  const int Header=56,MaxRecords=4194304;
  internal string Path {get;private set;}
  public ulong Length{get{return disk.Length;}}
  public uint SectorSize{get{return disk.SectorSize;}}
  static readonly byte[] Magic=Encoding.ASCII.GetBytes("NXCV0001");
  internal static byte[] Binding(string path,string serial,ulong size,uint sector) {
   using(var sha=SHA256.Create())return sha.ComputeHash(Encoding.UTF8.GetBytes(path+"\n"+serial+"\n"+size+"\n"+sector));
  }
  static bool Same(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  static void Need(bool test,string reason){if(!test)throw new IOException("Conversion journal refused: "+reason);}
  internal ConversionJournal(IBlockDevice disk,string path,byte[] identity,bool existing) {
   this.disk=disk;binding=identity;Path=path;
   Need(identity!=null&&identity.Length==32,"identity");
   Need(System.IO.Path.IsPathRooted(path)&&!path.StartsWith(@"\\")&&System.IO.Path.GetExtension(path)==".nxcv","regular local journal path required");
   file=new FileStream(path,existing?FileMode.Open:FileMode.CreateNew,existing?FileAccess.Read:FileAccess.ReadWrite,FileShare.None,1048576,FileOptions.WriteThrough);
   try {if(existing)Load();}catch{file.Dispose();throw;}
  }
  byte[] Bytes(long at,int size) {
   file.Position=at;byte[] b=new byte[size];int done=0;
   while(done<size){int n=file.Read(b,done,size-done);Need(n>0,"truncated file");done+=n;}return b;
  }
  void Put(byte[] b){file.Write(b,0,b.Length);}
  byte[] Digest(long length) {
   using(var sha=SHA256.Create()) {
    file.Position=0;byte[] buffer=new byte[1048576];
    while(length>0){int n=file.Read(buffer,0,(int)Math.Min(length,buffer.Length));Need(n>0,"short checksum read");sha.TransformBlock(buffer,0,n,buffer,0);length-=n;}
    sha.TransformFinalBlock(new byte[0],0,0);return sha.Hash;
   }
  }
  void CheckRanges() {
   sorted.Clear();sorted.AddRange(records);sorted.Sort((a,b)=>a.Offset.CompareTo(b.Offset));ulong end=0;
   foreach(var r in sorted){Need(r.Offset>=end&&r.Offset%SectorSize==0&&r.Length>0&&r.Length<=1048576&&r.Length%SectorSize==0&&r.Offset<=Length&&(ulong)r.Length<=Length-r.Offset,"overlapping or invalid target range");end=r.Offset+(ulong)r.Length;}
  }
  internal void Prepare(IEnumerable<ConversionChange> changes,Action check) {
   Need(!sealedFile&&file.Length==0,"journal already used");
   Put(Magic);Put(BitConverter.GetBytes(Length));Put(BitConverter.GetBytes(SectorSize));Put(binding);Put(BitConverter.GetBytes(0));
   foreach(var c in changes) {
    check();Need(records.Count<MaxRecords&&c.Data!=null&&c.Data.Length>0&&c.Data.Length<=1048576&&c.Offset%SectorSize==0&&c.Data.Length%SectorSize==0&&c.Offset<=Length&&(ulong)c.Data.Length<=Length-c.Offset,"change bounds/resource limit");
    byte[] before=disk.Read(c.Offset,c.Data.Length);Need(before.Length==c.Data.Length,"short preimage");
    // Exact no-ops need neither journal space nor device writes.
    if(Same(before,c.Data))continue;
    Put(BitConverter.GetBytes(c.Offset));Put(BitConverter.GetBytes(c.Data.Length));
    var record=new Record{Offset=c.Offset,Length=c.Data.Length,Before=file.Position};
    Put(before);record.After=file.Position;Put(c.Data);records.Add(record);
   }
   CheckRanges();file.Position=52;Put(BitConverter.GetBytes(records.Count));file.Flush(true);
   long end=file.Length;byte[] digest=Digest(end);file.Position=end;Put(digest);file.Flush(true);sealedFile=true;
   Verify();
  }
  void Load() {
   Need(file.Length>=Header+32,"header");
   byte[] h=Bytes(0,Header);
   Need(Same(Magic,Bytes(0,8))&&BitConverter.ToUInt64(h,8)==Length&&BitConverter.ToUInt32(h,16)==SectorSize&&Same(binding,Bytes(20,32)),"wrong drive identity/geometry");
   int count=BitConverter.ToInt32(h,52);Need(count>=0&&count<=MaxRecords,"record count");
   long position=Header;
   for(int i=0;i<count;i++) {
    Need(position<=file.Length-44,"record header");
    byte[] b=Bytes(position,12);int length=BitConverter.ToInt32(b,8);
    Need(length>0&&length<=1048576&&position+12+2L*length<=file.Length-32,"record length");
    records.Add(new Record{Offset=BitConverter.ToUInt64(b,0),Length=length,Before=position+12,After=position+12+length});
    position+=12+2L*length;
   }
   Need(position==file.Length-32,"trailing/missing records");CheckRanges();sealedFile=true;Verify();
  }
  internal void Verify(){Need(sealedFile&&Same(Digest(file.Length-32),Bytes(file.Length-32,32)),"checksum mismatch");}
  internal void Apply(Action check,Action writesStarted) {
   Verify();
   foreach(var r in records){check();Need(Same(disk.Read(r.Offset,r.Length),Bytes(r.Before,r.Length)),"source changed after staging; no writes started");}
   if(records.Count==0)return;
   writesStarted();
   foreach(var r in records){check();disk.Write(r.Offset,Bytes(r.After,r.Length));}
   disk.Flush();
   foreach(var r in records){check();Need(Same(disk.Read(r.Offset,r.Length),Bytes(r.After,r.Length)),"converted readback mismatch; recovery required");}
  }
  internal void Restore(Action check,Action writesStarted) {
   Verify();if(records.Count==0)return;writesStarted();
   // Reverse publication order: unpublish new boot/partition metadata first.
   for(int i=records.Count-1;i>=0;i--){check();var r=records[i];disk.Write(r.Offset,Bytes(r.Before,r.Length));}
   disk.Flush();
   foreach(var r in records){check();Need(Same(disk.Read(r.Offset,r.Length),Bytes(r.Before,r.Length)),"recovery readback mismatch");}
  }
  // Read-only virtual result, used to re-parse and hash every file BEFORE apply.
  public byte[] Read(ulong at,int size) {
   Need(sealedFile&&at%SectorSize==0&&size>0&&size%SectorSize==0&&at<=Length&&(ulong)size<=Length-at,"virtual read bounds");
   byte[] data=disk.Read(at,size);Need(data.Length==size,"short virtual base read");
   int lo=0,hi=sorted.Count;
   while(lo<hi){int mid=lo+(hi-lo)/2;if(sorted[mid].Offset+(ulong)sorted[mid].Length<=at)lo=mid+1;else hi=mid;}
   for(int i=lo;i<sorted.Count&&sorted[i].Offset<at+(ulong)size;i++) {
    var r=sorted[i];ulong begin=Math.Max(at,r.Offset),end=Math.Min(at+(ulong)size,r.Offset+(ulong)r.Length);
    byte[] b=Bytes(r.After+checked((long)(begin-r.Offset)),checked((int)(end-begin)));Array.Copy(b,0,data,(long)(begin-at),b.Length);
   }
   return data;
  }
  public void Write(ulong at,byte[] data){throw new InvalidOperationException("Journal preview is read-only.");}
  public void Flush(){throw new InvalidOperationException("Journal preview is read-only.");}
  public void Dispose(){file.Dispose();}
 }
}
