// Nexus-owned MBR/GPT + exFAT quick formatter. No platform formatting API.
// References: Microsoft exFAT specification; UEFI 2.10 chapter 5.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusFormatter {
 internal interface IBlockDevice {
  ulong Length {get;}
  uint SectorSize {get;}
  void Write(ulong offset,byte[] bytes);
  byte[] Read(ulong offset,int count);
  void Flush();
 }
 internal sealed class SectorRun {
  internal ulong Offset,Length;
  internal byte[] Bytes; // null means a bounded zero-fill run, not an allocation of Length bytes.
  internal SectorRun(ulong offset,ulong length,byte[] bytes){Offset=offset;Length=length;Bytes=bytes;}
 }
 internal sealed class ExFatPlan {
  internal ulong DiskSize,DataOffset,DataSize,FatOffset,FatLength,HeapOffset,BitmapBytes;
  internal uint Sector,Clusters,BitmapClusters,UpcaseCluster,RootCluster;
  internal string Style,Label;
  internal readonly List<SectorRun> Metadata=new List<SectorRun>();
  internal readonly List<SectorRun> Publication=new List<SectorRun>();
  internal byte[] Upcase;
 }
 internal static class ManagedExFat {
  internal const ulong Cluster=524288,MiB=1048576;
  internal static ulong Align(ulong value,ulong unit){return checked((value+unit-1)/unit*unit);}
  internal static void Put16(byte[] b,int at,ushort v){b[at]=(byte)v;b[at+1]=(byte)(v>>8);}
  internal static void Put32(byte[] b,int at,uint v){for(int i=0;i<4;i++)b[at+i]=(byte)(v>>(i*8));}
  internal static void Put64(byte[] b,int at,ulong v){for(int i=0;i<8;i++)b[at+i]=(byte)(v>>(i*8));}
  internal static uint Crc(byte[] b,int count) {
   uint crc=0xffffffff;for(int i=0;i<count;i++){crc^=b[i];for(int j=0;j<8;j++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
   return ~crc;
  }
  internal static uint Checksum(byte[] b,int count,bool boot) {
   uint sum=0;for(int i=0;i<count;i++)if(!boot||(i!=106&&i!=107&&i!=112))sum=unchecked((sum>>1)|(sum<<31))+b[i];
   return sum;
  }
  internal static ExFatPlan Plan(ulong size,uint sector,string style,string label,Guid diskId,Guid dataId,uint serial) {
   if((sector!=512&&sector!=4096)||size<1073741824UL||size>140737488355328UL||size%sector!=0)
    throw new InvalidOperationException("Unsupported native format geometry.");
   if(style!="MBR"&&style!="GPT")throw new InvalidOperationException("Unknown native partition style.");
   if(style=="MBR"&&size>2000000000000UL)throw new InvalidOperationException("Above 2 TB requires GPT; no truncation.");
   if(label==null||!Regex.IsMatch(label,@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,10}\z")||label!=label.Trim())throw new InvalidOperationException("Invalid volume label (1-11 characters).");
   if(diskId==Guid.Empty||dataId==Guid.Empty||diskId==dataId||serial==0)throw new InvalidOperationException("Invalid format identifiers.");
   var p=new ExFatPlan{DiskSize=size,Sector=sector,Style=style,Label=label,DataOffset=style=="GPT"?17*MiB:MiB};
   ulong tableSectors=16384UL/sector;
   ulong end=(style=="GPT"?size-(tableSectors+1)*sector:size)/MiB*MiB;
   p.DataSize=end-p.DataOffset;
   // Use an upper bound for FAT allocation; avoids fixed-point oscillation.
   p.FatOffset=MiB;p.FatLength=Align((p.DataSize/Cluster+2)*4,sector);
   p.HeapOffset=Align(p.FatOffset+p.FatLength,MiB);
   p.Clusters=checked((uint)((p.DataSize-p.HeapOffset)/Cluster));
   if(p.Clusters<16||p.Clusters>0xfffffff5u||p.HeapOffset/sector>uint.MaxValue)throw new InvalidOperationException("Invalid exFAT cluster geometry.");
   p.BitmapBytes=(p.Clusters+7UL)/8;
   p.BitmapClusters=checked((uint)(Align(p.BitmapBytes,Cluster)/Cluster));
   p.UpcaseCluster=2+p.BitmapClusters;p.RootCluster=p.UpcaseCluster+1;
   if(p.RootCluster>=p.Clusters+2UL)throw new InvalidOperationException("Insufficient metadata clusters.");
   p.Upcase=ExFatUpcase.Get();
   // First invalidate old partition headers and old backup GPT. No secure erase.
   p.Metadata.Add(new SectorRun(0,MiB,null));
   p.Metadata.Add(new SectorRun(size-MiB,MiB,null));
   if(style=="GPT")p.Metadata.Add(new SectorRun(MiB,16*MiB,null));
   // Zero all filesystem metadata; unallocated game-data clusters are untouched.
   p.Metadata.Add(new SectorRun(p.DataOffset,p.HeapOffset+(p.RootCluster-1UL)*Cluster,null));
   byte[] fat=new byte[(int)Align((p.RootCluster+1UL)*4,sector)];
   Put32(fat,0,0xfffffff8);Put32(fat,4,0xffffffff);
   for(uint c=2;c<2+p.BitmapClusters;c++)Put32(fat,(int)c*4,c==1+p.BitmapClusters?0xffffffff:c+1);
   Put32(fat,(int)p.UpcaseCluster*4,0xffffffff);Put32(fat,(int)p.RootCluster*4,0xffffffff);
   p.Metadata.Add(new SectorRun(p.DataOffset+p.FatOffset,(ulong)fat.Length,fat));
   byte[] bitmap=new byte[(int)Align((p.RootCluster-1UL+7)/8,sector)];
   for(uint c=2;c<=p.RootCluster;c++)bitmap[(c-2)/8]|=(byte)(1<<((int)(c-2)%8));
   p.Metadata.Add(new SectorRun(p.DataOffset+p.HeapOffset,(ulong)bitmap.Length,bitmap));
   byte[] upcaseSectors=new byte[(int)Align((ulong)p.Upcase.Length,sector)];
   Array.Copy(p.Upcase,upcaseSectors,p.Upcase.Length);
   p.Metadata.Add(new SectorRun(p.DataOffset+p.HeapOffset+(p.UpcaseCluster-2UL)*Cluster,(ulong)upcaseSectors.Length,upcaseSectors));
   byte[] root=new byte[sector];root[0]=0x83;root[1]=(byte)label.Length;
   Array.Copy(Encoding.Unicode.GetBytes(label),0,root,2,label.Length*2);
   root[32]=0x81;Put32(root,52,2);Put64(root,56,p.BitmapBytes);
   root[64]=0x82;Put32(root,68,Checksum(p.Upcase,p.Upcase.Length,false));Put32(root,84,p.UpcaseCluster);Put64(root,88,(ulong)p.Upcase.Length);
   p.Metadata.Add(new SectorRun(p.DataOffset+p.HeapOffset+(p.RootCluster-2UL)*Cluster,sector,root));
   byte[] boot=new byte[12*sector];boot[0]=0xeb;boot[1]=0x76;boot[2]=0x90;
   Array.Copy(Encoding.ASCII.GetBytes("EXFAT   "),0,boot,3,8);
   Put64(boot,64,p.DataOffset/sector);Put64(boot,72,p.DataSize/sector);
   Put32(boot,80,(uint)(p.FatOffset/sector));Put32(boot,84,(uint)(p.FatLength/sector));
   Put32(boot,88,(uint)(p.HeapOffset/sector));Put32(boot,92,p.Clusters);Put32(boot,96,p.RootCluster);Put32(boot,100,serial);
   Put16(boot,104,0x100);boot[108]=(byte)(sector==512?9:12);boot[109]=(byte)(sector==512?10:7);boot[110]=1;boot[111]=0x80;
   boot[112]=(byte)(((p.RootCluster-1UL)*100)/p.Clusters);boot[510]=0x55;boot[511]=0xaa;
   for(int s=1;s<=8;s++)Put32(boot,(int)((s+1)*sector)-4,0xaa550000);
   uint sum=Checksum(boot,(int)(11*sector),true);for(int i=(int)(11*sector);i<boot.Length;i+=4)Put32(boot,i,sum);
   // Publish backup boot then main boot only after all allocation metadata.
   p.Publication.Add(new SectorRun(p.DataOffset+12*sector,(ulong)boot.Length,(byte[])boot.Clone()));
   p.Publication.Add(new SectorRun(p.DataOffset,(ulong)boot.Length,boot));
   byte[] mbr=new byte[sector];Put32(mbr,440,serial);mbr[510]=0x55;mbr[511]=0xaa;
   mbr[447]=0xfe;mbr[448]=0xff;mbr[449]=0xff;mbr[451]=0xfe;mbr[452]=0xff;mbr[453]=0xff;
   if(style=="MBR") {
    mbr[450]=7;Put32(mbr,454,checked((uint)(p.DataOffset/sector)));Put32(mbr,458,checked((uint)(p.DataSize/sector)));
   } else {
    mbr[447]=0;mbr[448]=2;mbr[449]=0;mbr[450]=0xee;Put32(mbr,454,1);Put32(mbr,458,(uint)Math.Min(size/sector-1,uint.MaxValue));
    byte[] entries=new byte[16384];
    PutEntry(entries,0,new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae"),Guid.NewGuid(),MiB/sector,17*MiB/sector-1,"Microsoft reserved");
    PutEntry(entries,128,new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),dataId,p.DataOffset/sector,end/sector-1,"NEXUS_USB");
    ulong backup=size/sector-1;
    p.Publication.Add(new SectorRun((backup-tableSectors)*sector,16384,(byte[])entries.Clone()));
    p.Publication.Add(new SectorRun(backup*sector,sector,GptHeader(p,diskId,backup,1,backup-tableSectors,entries)));
    p.Publication.Add(new SectorRun(2*sector,16384,entries));
    p.Publication.Add(new SectorRun(sector,sector,GptHeader(p,diskId,1,backup,2,entries)));
   }
   p.Publication.Add(new SectorRun(0,sector,mbr)); // disk discoverability last
   foreach(var run in All(p))if(run.Offset%sector!=0||run.Length%sector!=0||run.Offset>size||run.Length>size-run.Offset||
     (run.Bytes!=null&&(ulong)run.Bytes.Length!=run.Length))throw new InvalidOperationException("Unsafe format plan extent.");
   return p;
  }
  static void PutEntry(byte[] b,int at,Guid type,Guid id,ulong first,ulong last,string name) {
   Array.Copy(type.ToByteArray(),0,b,at,16);Array.Copy(id.ToByteArray(),0,b,at+16,16);Put64(b,at+32,first);Put64(b,at+40,last);
   byte[] text=Encoding.Unicode.GetBytes(name);Array.Copy(text,0,b,at+56,Math.Min(72,text.Length));
  }
  static byte[] GptHeader(ExFatPlan p,Guid diskId,ulong here,ulong other,ulong entriesAt,byte[] entries) {
   byte[] b=new byte[p.Sector];Array.Copy(Encoding.ASCII.GetBytes("EFI PART"),b,8);Put32(b,8,0x10000);Put32(b,12,92);
   Put64(b,24,here);Put64(b,32,other);Put64(b,40,2+16384UL/p.Sector);Put64(b,48,p.DiskSize/p.Sector-2-16384UL/p.Sector);
   Array.Copy(diskId.ToByteArray(),0,b,56,16);Put64(b,72,entriesAt);Put32(b,80,128);Put32(b,84,128);Put32(b,88,Crc(entries,entries.Length));Put32(b,16,Crc(b,92));return b;
  }
  static IEnumerable<SectorRun> All(ExFatPlan p) {foreach(var r in p.Metadata)yield return r;foreach(var r in p.Publication)yield return r;}
  static void Runs(IBlockDevice disk,IEnumerable<SectorRun> runs,Action check) {
   foreach(var run in runs)for(ulong at=0;at<run.Length;) {
    check();int count=(int)Math.Min(MiB,run.Length-at);byte[] data=new byte[count];
    if(run.Bytes!=null)Array.Copy(run.Bytes,(long)at,data,0,count);
    disk.Write(run.Offset+at,data);at+=(ulong)count;
   }
  }
  internal static void Format(IBlockDevice disk,ExFatPlan plan,Action check,Action<string> progress) {
   if(disk.Length!=plan.DiskSize||disk.SectorSize!=plan.Sector)throw new InvalidOperationException("Format handle geometry differs from plan.");
   check();progress("Writing partition and exFAT allocation metadata; all target volume locks held.");
   Runs(disk,plan.Metadata,check);disk.Flush();
   progress("Publishing exFAT boot regions and redundant partition tables.");
   Runs(disk,plan.Publication,check);disk.Flush();check();
   progress("Reading back all written metadata (not a file-data surface test).");
   // Check final bytes, including zero-fill regions overridden by later writes.
   var overlays=new List<SectorRun>(All(plan));
   foreach(var run in overlays)for(ulong at=0;at<run.Length;) {
    check();int count=(int)Math.Min(MiB,run.Length-at);ulong start=run.Offset+at,end=start+(ulong)count;
    byte[] expected=new byte[count];
    foreach(var layer in overlays) {
     ulong lo=Math.Max(start,layer.Offset),hi=Math.Min(end,layer.Offset+layer.Length);if(lo>=hi)continue;
     if(layer.Bytes==null)Array.Clear(expected,(int)(lo-start),(int)(hi-lo));
     else Array.Copy(layer.Bytes,(long)(lo-layer.Offset),expected,(long)(lo-start),(long)(hi-lo));
    }
    byte[] observed=disk.Read(start,count);if(observed.Length!=count)throw new IOException("Short metadata readback.");
    for(int i=0;i<count;i++)if(expected[i]!=observed[i])throw new IOException("Metadata readback mismatch at byte "+(start+(ulong)i)+". Do not use the drive.");
    at+=(ulong)count;
   }
   check();
  }
 }
}
