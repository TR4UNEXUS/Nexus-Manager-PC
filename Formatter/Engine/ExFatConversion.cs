// File-preserving exFAT reclustering. No Windows formatter and no disk writes.
// The caller must hold an exclusive lease and stage/validate all changes first.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace NexusFormatter {
 internal sealed class ConversionChange {
  internal ulong Offset; internal byte[] Data;
  internal ConversionChange(ulong offset,byte[] data){Offset=offset;Data=data;}
 }
 internal sealed class ExFatConversion {
  internal sealed class Node {
   internal List<uint> Chain; internal byte[] Directory,Identity;
   internal bool IsDirectory; internal ulong Length,Valid; internal uint NewFirst,NewCount;
   internal int ParentOffset; internal Node Parent;
  }
  readonly IBlockDevice disk; readonly ulong start,length,heap,fat,fatLength;
  readonly uint sector,cluster,count; readonly byte[] boot,bitmap,upcase;
  readonly BitArray claimed; readonly List<Node> nodes=new List<Node>();
  readonly Dictionary<ulong,byte[]> fatCache=new Dictionary<ulong,byte[]>();
  internal uint ClusterBytes {get{return cluster;}}
  internal int FileCount {get{return nodes.FindAll(n=>!n.IsDirectory).Count;}}
  internal ulong FileBytes {get{ulong sum=0;foreach(var n in nodes)if(!n.IsDirectory)sum=checked(sum+n.Length);return sum;}}
  internal ulong PartitionOffset {get{return start;}}
  internal ulong PartitionLength {get{return length;}}
  static void Need(bool ok,string reason){if(!ok)throw new IOException("Conversion refused: "+reason);}
  static uint U32(byte[] b,int at){return BitConverter.ToUInt32(b,at);}
  static ulong U64(byte[] b,int at){return BitConverter.ToUInt64(b,at);}
  internal static ushort EntryChecksum(byte[] b,int at,int size) {
   ushort v=0;for(int i=0;i<size;i++)if(i!=2&&i!=3)v=unchecked((ushort)(((v&1)<<15)+(v>>1)+b[at+i]));return v;
  }
  byte[] Read(ulong offset,int size) {
   Need(offset%sector==0&&size>0&&size%sector==0&&offset<=length&&(ulong)size<=length-offset,"read outside volume");
   byte[] b=disk.Read(checked(start+offset),size);Need(b.Length==size,"short volume read");return b;
  }
  static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  internal ExFatConversion(IBlockDevice disk,ulong offset,ulong partitionLength) {
   this.disk=disk;start=offset;length=partitionLength;sector=disk.SectorSize;
   Need((sector==512||sector==4096)&&offset%sector==0&&partitionLength%sector==0&&offset>=1048576&&offset<=disk.Length&&partitionLength<=disk.Length-offset,"partition geometry");
   boot=Read(0,checked((int)(24*sector)));
   Need(Encoding.ASCII.GetString(boot,3,8)=="EXFAT   "&&boot[108]==(sector==512?9:12)&&boot[109]<=25-boot[108],"exFAT boot geometry");
   cluster=1u<<(boot[108]+boot[109]);count=U32(boot,92);
   Need(cluster<=524288&&count>=16&&count<=268435456,"only cluster sizes up to 512 KB and bounded allocation maps are supported");
   Need(BitConverter.ToUInt16(boot,104)==0x100&&BitConverter.ToUInt16(boot,106)==0&&boot[110]==1,"dirty, damaged, TexFAT or unsupported volume");
   Need(U64(boot,64)==offset/sector&&U64(boot,72)<=partitionLength/sector&&U64(boot,72)>=24,"volume identity/length");
   heap=(ulong)U32(boot,88)*sector;fat=(ulong)U32(boot,80)*sector;fatLength=(ulong)U32(boot,84)*sector;
   Need(fat>=24*sector&&fatLength>0&&heap>=fat+fatLength&&heap<=U64(boot,72)*sector&&(ulong)count*cluster<=U64(boot,72)*sector-heap&&fatLength>=((ulong)count+2)*4,"allocation bounds");
   for(int region=0;region<2;region++) {
    byte[] b=new byte[12*sector];Array.Copy(boot,region*b.Length,b,0,b.Length);
    uint checksum=ManagedExFat.Checksum(b,checked((int)(11*sector)),true);
    for(int i=checked((int)(11*sector));i<b.Length;i+=4)Need(U32(b,i)==checksum,"boot checksum");
    Need(b[510]==0x55&&b[511]==0xaa,"boot signature");
   }
   for(int i=0;i<12*sector;i++)if(i!=106&&i!=107&&i!=112)Need(boot[i]==boot[12*sector+i],"main/backup boot mismatch");
   Need(Fat(0)==0xfffffff8&&Fat(1)==0xffffffff,"reserved FAT entries");
   claimed=new BitArray(checked((int)count));
   var root=new Node{IsDirectory=true,Chain=Chain(U32(boot,96),0,false,true)};
   root.Length=root.Valid=(ulong)root.Chain.Count*cluster;
   root.Directory=ReadAll(root,32*1024*1024);nodes.Add(root);
   uint bitmapFirst=0,upcaseFirst=0;ulong bitmapSize=0,upcaseSize=0;uint upcaseSum=0;
   for(int at=0;at<root.Directory.Length&&root.Directory[at]!=0;at+=32) {
    byte type=root.Directory[at];if((type&128)==0)continue;
    if(type==0x81){Need(bitmapFirst==0&&root.Directory[at+1]==0,"multiple/active FAT bitmaps");bitmapFirst=U32(root.Directory,at+20);bitmapSize=U64(root.Directory,at+24);}
    else if(type==0x82){Need(upcaseFirst==0,"multiple upcase tables");upcaseSum=U32(root.Directory,at+4);upcaseFirst=U32(root.Directory,at+20);upcaseSize=U64(root.Directory,at+24);}
    else if(type==0x85){int entries=root.Directory[at+1]+1;Need(entries>=3&&at+entries*32<=root.Directory.Length,"root entry set");at+=(entries-1)*32;}
    else Need(type==0x83,"unsupported root directory entry");
   }
   Need(bitmapSize==((ulong)count+7)/8&&upcaseSize>0&&upcaseSize<=131072,"bitmap/upcase sizes");
   var bitNode=new Node{Length=bitmapSize,Valid=bitmapSize,Chain=Chain(bitmapFirst,bitmapSize,false,false)};
   bitmap=ReadAll(bitNode,64*1024*1024);
   var upNode=new Node{Length=upcaseSize,Valid=upcaseSize,Chain=Chain(upcaseFirst,upcaseSize,false,false)};
   upcase=ReadAll(upNode,131072);Need(ManagedExFat.Checksum(upcase,upcase.Length,false)==upcaseSum,"upcase checksum");
   Parse(root,0);
   for(int i=0;i<count;i++)Need(claimed[i]==((bitmap[i/8]&(1<<(i%8)))!=0),"allocated clusters are orphaned, cross-linked or missing");
  }
  uint Fat(uint c) {
   ulong at=(ulong)c*4,page=at/65536*65536;byte[] b;
   if(!fatCache.TryGetValue(page,out b)) {
    Need(page<fatLength,"FAT index");int size=(int)Math.Min(65536UL,fatLength-page);
    b=Read(fat+page,size);if(fatCache.Count>=32)fatCache.Clear();fatCache.Add(page,b);
   }
   return U32(b,(int)(at-page));
  }
  List<uint> Chain(uint first,ulong bytes,bool contiguous,bool root) {
   var result=new List<uint>();if(!root&&bytes==0){Need(first==0,"empty stream has allocated cluster");return result;}
   ulong wanted=root?Math.Min(count,8388608u):(bytes+cluster-1)/cluster;
   Need(wanted<=count&&wanted<=8388608,"stream allocation exceeds conversion resource limit");
   uint c=first;
   for(ulong i=0;i<wanted;i++) {
    Need(c>=2&&c<(ulong)count+2&&!claimed[(int)c-2],"invalid, cyclic or shared cluster chain");
    claimed[(int)c-2]=true;result.Add(c);
    if(contiguous){c++;continue;}
    uint next=Fat(c);
    if(root&&next>=0xfffffff8)return result;
    if(i+1==wanted){Need(next>=0xfffffff8,"stream has excess FAT allocation");break;}
    Need(next<0xfffffff8,"short FAT chain");c=next;
   }
   Need(!root,"unterminated root FAT chain");return result;
  }
  byte[] ReadAll(Node n,int maximum) {
   Need(n.Length<=(ulong)maximum,"directory/metadata exceeds conversion resource limit");
   var data=new byte[(int)n.Length];ulong at=0;
   foreach(byte[] b in Content(n)){Array.Copy(b,0,data,(long)at,b.Length);at+=(ulong)b.Length;}
   return data;
  }
  IEnumerable<byte[]> Content(Node n) {
   for(ulong at=0;at<n.Length;) {
    int size=(int)Math.Min(1048576UL,n.Length-at);byte[] data=new byte[size];int filled=0;
    while(filled<size&&at+(ulong)filled<n.Valid) {
     ulong pos=at+(ulong)filled;int ix=checked((int)(pos/cluster)),within=(int)(pos%cluster);
     int take=(int)Math.Min((ulong)Math.Min(size-filled,(int)cluster-within),n.Valid-pos);
     ulong absolute=heap+(n.Chain[ix]-2UL)*cluster+(uint)within;
     // at and within remain sector-aligned except at the final valid byte.
     byte[] b=Read(absolute,checked((int)ManagedExFat.Align((ulong)take,sector)));
     Array.Copy(b,0,data,filled,take);filled+=take;
    }
    yield return data;at+=(ulong)size;
   }
  }
  void Parse(Node directory,int depth) {
   Need(depth<=64&&nodes.Count<=100000,"directory tree exceeds conversion resource limit");
   byte[] original=directory.Directory??ReadAll(directory,32*1024*1024);
   using(var output=new MemoryStream()) {
    for(int at=0;at<original.Length&&original[at]!=0;) {
     byte type=original[at];if((type&128)==0){at+=32;continue;}
     if(type==0x81||type==0x82||type==0x83) {
      Need(directory.Parent==null,"root-only metadata in subdirectory");
      output.Write(original,at,32);at+=32;continue;
     }
     Need(type==0x85&&at+32<=original.Length,"unsupported directory entry");
     int size=(original[at+1]+1)*32;
     Need(size>=96&&at+size<=original.Length&&original[at+32]==0xc0,"incomplete file entry set");
     Need(EntryChecksum(original,at,size)==BitConverter.ToUInt16(original,at+2),"file entry checksum");
     int nameLength=original[at+35],names=(nameLength+14)/15;
     Need(nameLength>0&&size==(2+names)*32,"unknown file secondary entries");
     for(int i=0;i<names;i++)Need(original[at+64+32*i]==0xc1,"unsupported filename entry");
     byte flags=original[at+33];Need(flags==1||flags==3,"unsupported stream flags");
     ulong bytes=U64(original,at+56),valid=U64(original,at+40);
     bool isDir=(BitConverter.ToUInt16(original,at+4)&16)!=0;
     Need(valid<=bytes&&(bytes!=0||flags==1)&&(!isDir||(bytes>0&&bytes%cluster==0&&valid==bytes)),"stream length/empty-file flags");
     byte[] identity=new byte[size];Array.Copy(original,at,identity,0,size);
     var n=new Node{Parent=directory,ParentOffset=checked((int)output.Position),IsDirectory=isDir,Length=bytes,Valid=valid,Identity=identity,
      Chain=Chain(U32(original,at+52),bytes,(flags&2)!=0,false)};
     nodes.Add(n);Need(nodes.Count<=100000,"directory tree exceeds conversion resource limit");output.Write(original,at,size);at+=size;
     if(isDir)Parse(n,depth+1);
    }
    output.Write(new byte[32],0,32);directory.Directory=output.ToArray();
   }
  }
  internal byte[] FileManifest() {
   using(var sha=SHA256.Create()) {
    foreach(var n in nodes) {
     byte[] meta=n.Identity==null?new byte[0]:(byte[])n.Identity.Clone();
     if(meta.Length!=0) {
      meta[2]=meta[3]=0;meta[33]=1;Array.Clear(meta,52,4);
      if(n.IsDirectory){Array.Clear(meta,40,8);Array.Clear(meta,56,8);}
     }
     sha.TransformBlock(meta,0,meta.Length,meta,0);
     if(!n.IsDirectory)foreach(var b in Content(n))sha.TransformBlock(b,0,b.Length,b,0);
    }
    sha.TransformFinalBlock(new byte[0],0,0);return sha.Hash;
   }
  }
  internal IEnumerable<ConversionChange> Recluster512() {
   Need(cluster<524288,"already uses 512 KB clusters");
   uint newCount=checked((uint)((ulong)count*cluster/524288));
   ulong bitmapBytes=((ulong)newCount+7)/8;uint bitClusters=(uint)ManagedExFat.Align(bitmapBytes,524288)/524288;
   uint upFirst=2+bitClusters,next=upFirst+(uint)ManagedExFat.Align((ulong)upcase.Length,524288)/524288;
   foreach(var n in nodes) {
    ulong bytes=n.IsDirectory?(ulong)n.Directory.Length:n.Length;
    n.NewCount=checked((uint)(ManagedExFat.Align(bytes,524288)/524288));
    n.NewFirst=n.NewCount==0?0:next;next=checked(next+n.NewCount);
   }
   Need(next<=newCount+2UL,"insufficient free space for 512 KB allocation units");
   foreach(var n in nodes)if(n.Parent!=null) {
    byte[] entry=n.Parent.Directory;int at=n.ParentOffset;
    entry[at+33]=(byte)(n.NewCount==0?1:3);ManagedExFat.Put32(entry,at+52,n.NewFirst);
    if(n.IsDirectory){ManagedExFat.Put64(entry,at+40,n.NewCount*524288UL);ManagedExFat.Put64(entry,at+56,n.NewCount*524288UL);}
    ManagedExFat.Put16(entry,at+2,EntryChecksum(entry,at,(entry[at+1]+1)*32));
   }
   var root=nodes[0];
   for(int at=0;at<root.Directory.Length&&root.Directory[at]!=0;at+=32) {
    byte type=root.Directory[at];
    if(type==0x81){ManagedExFat.Put32(root.Directory,at+20,2);ManagedExFat.Put64(root.Directory,at+24,bitmapBytes);}
    if(type==0x82)ManagedExFat.Put32(root.Directory,at+20,upFirst);
    if(type==0x85)at+=root.Directory[at+1]*32;
   }
   // All original payload is read while preparing a PC journal, before ANY disk write.
   foreach(var n in nodes) {
    ulong position=start+heap+(n.NewFirst==0?0:n.NewFirst-2UL)*524288;
    if(n.IsDirectory) {
     foreach(var change in Padded(position,n.Directory,n.NewCount*524288UL))yield return change;
    } else {
     ulong emitted=0;
     foreach(var chunk in Content(n)) {
      int size=(int)ManagedExFat.Align((ulong)chunk.Length,524288);byte[] data=new byte[size];Array.Copy(chunk,data,chunk.Length);
      yield return new ConversionChange(position+emitted,data);emitted+=(ulong)size;
     }
     Need(emitted==n.NewCount*524288UL,"payload extent mismatch");
    }
   }
   byte[] bits=new byte[checked((int)bitmapBytes)];for(uint i=0;i<next-2;i++)bits[i/8]|=(byte)(1<<(int)(i%8));
   foreach(var change in Padded(start+heap,bits,bitClusters*524288UL))yield return change;
   foreach(var change in Padded(start+heap+(upFirst-2UL)*524288,upcase,524288))yield return change;
   ulong newFatBytes=ManagedExFat.Align(((ulong)newCount+2)*4,sector);
   for(ulong at=0;at<newFatBytes;at+=1048576) {
    byte[] data=new byte[(int)Math.Min(1048576UL,newFatBytes-at)];
    for(int i=0;i<data.Length;i+=4) {
     ulong c=(at+(ulong)i)/4;uint value=0;
     if(c==0)value=0xfffffff8;else if(c==1)value=0xffffffff;
     else if(c>=2&&c<2+bitClusters)value=c==1+bitClusters?0xffffffff:(uint)c+1;
     else if(c==upFirst)value=0xffffffff;
     else if(c>=root.NewFirst&&c<root.NewFirst+root.NewCount)value=c==root.NewFirst+root.NewCount-1?0xffffffff:(uint)c+1;
     ManagedExFat.Put32(data,i,value);
    }
    yield return new ConversionChange(start+fat+at,data);
   }
   byte[] main=new byte[12*sector];Array.Copy(boot,main,main.Length);
   main[109]=(byte)(sector==512?10:7);ManagedExFat.Put32(main,92,newCount);ManagedExFat.Put32(main,96,root.NewFirst);
   main[112]=(byte)((next-2UL)*100/newCount);
   uint sum=ManagedExFat.Checksum(main,(int)(11*sector),true);
   for(int i=(int)(11*sector);i<main.Length;i+=4)ManagedExFat.Put32(main,i,sum);
   yield return new ConversionChange(start+12*sector,(byte[])main.Clone());
   yield return new ConversionChange(start,main);
  }
  static IEnumerable<ConversionChange> Padded(ulong at,byte[] source,ulong length) {
   for(ulong pos=0;pos<length;pos+=1048576) {
    byte[] b=new byte[(int)Math.Min(1048576UL,length-pos)];
    if(pos<(ulong)source.Length)Array.Copy(source,(long)pos,b,0,(int)Math.Min((ulong)b.Length,(ulong)source.Length-pos));
    yield return new ConversionChange(at+pos,b);
   }
  }
 }
}
