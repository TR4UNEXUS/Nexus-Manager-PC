// Basic USB partition-table conversion without moving/resizing the exFAT volume.
// Refuses hybrid, extended, boot/system, multi-data and insufficient-tail layouts.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace NexusFormatter {
 internal static class PartitionConversion {
  static readonly Guid Basic=new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),Reserved=new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae");
  static void Need(bool ok,string reason){if(!ok)throw new IOException("Partition conversion refused: "+reason);}
  static uint U32(byte[] b,int n){return BitConverter.ToUInt32(b,n);}
  static ulong U64(byte[] b,int n){return BitConverter.ToUInt64(b,n);}
  static byte[] Slice(byte[] b,int n,int count){byte[] r=new byte[count];Array.Copy(b,n,r,0,count);return r;}
  static bool Equal(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  internal static List<ConversionChange> Plan(IBlockDevice disk,string oldStyle,string newStyle,ulong offset,ulong length) {
   uint sector=disk.SectorSize;
   Need((sector==512||sector==4096)&&disk.Length%sector==0&&(oldStyle=="MBR"||oldStyle=="GPT")&&(newStyle=="MBR"||newStyle=="GPT"),"geometry/style");
   ulong sectors=disk.Length/sector,entrySectors=16384UL/sector;
   Need(offset>=1048576&&offset%sector==0&&length>0&&length%sector==0&&offset<=disk.Length&&length<=disk.Length-offset,"volume bounds");
   byte[] mbr=disk.Read(0,(int)sector);Need(mbr.Length==sector&&mbr[510]==0x55&&mbr[511]==0xaa,"MBR signature");
   if(oldStyle=="MBR") {
    int data=0;
    for(int i=0;i<4;i++) {
     int at=446+i*16;uint size=U32(mbr,at+12);if(size==0){Need(mbr[at+4]==0,"partial MBR entry");continue;}
     Need(mbr[at]==0&&mbr[at+4]==7&&(ulong)U32(mbr,at+8)*sector==offset&&(ulong)size*sector==length,"requires one non-boot type-7 data partition");data++;
    }
    Need(data==1,"ambiguous MBR data partitions");
   } else {
    Need(mbr[446]==0&&mbr[450]==0xee&&U32(mbr,454)==1&&U32(mbr,458)==Math.Min(sectors-1,uint.MaxValue),"protective MBR");
    for(int i=462;i<510;i++)Need(mbr[i]==0,"hybrid MBR");
    byte[] first=Header(disk,1,sectors-1,entrySectors),last=Header(disk,sectors-1,1,entrySectors);
    Need(Equal(Slice(first,56,16),Slice(last,56,16))&&U64(first,40)==U64(last,40)&&U64(first,48)==U64(last,48),"GPT header mirrors");
    byte[] entries=disk.Read(U64(first,72)*sector,16384),backup=disk.Read(U64(last,72)*sector,16384);
    Need(Equal(entries,backup)&&ManagedExFat.Crc(entries,16384)==U32(first,88)&&U32(first,88)==U32(last,88),"GPT entries CRC/mirror");
    int data=0,reserved=0;var ranges=new List<Tuple<ulong,ulong>>();
    for(int at=0;at<16384;at+=128) {
     Guid type=new Guid(Slice(entries,at,16));if(type==Guid.Empty)continue;
     ulong begin=U64(entries,at+32),end=U64(entries,at+40);
     Need(begin>=U64(first,40)&&end>=begin&&end<=U64(first,48)&&U64(entries,at+48)==0,"GPT partition bounds/attributes");
     ranges.Add(Tuple.Create(begin,end));
     if(type==Basic){Need(begin*sector==offset&&(end-begin+1)*sector==length,"different data partition");data++;}
     else {Need(type==Reserved&&(end-begin+1)*sector<=134217728&&end*sector<offset,"unsupported GPT partition");reserved++;}
    }
    ranges.Sort((a,b)=>a.Item1.CompareTo(b.Item1));for(int i=1;i<ranges.Count;i++)Need(ranges[i].Item1>ranges[i-1].Item2,"overlapping GPT entries");
    Need(data==1&&reserved<=1,"requires one data partition with at most one Microsoft reservation");
   }
   var changes=new List<ConversionChange>();if(oldStyle==newStyle)return changes;
   byte[] replacement=(byte[])mbr.Clone();Array.Clear(replacement,446,64);
   replacement[447]=0xfe;replacement[448]=0xff;replacement[449]=0xff;
   replacement[451]=0xfe;replacement[452]=0xff;replacement[453]=0xff;
   if(newStyle=="GPT") {
    Need(offset/sector>=2+entrySectors&&(offset+length)/sector<=sectors-1-entrySectors,"not enough unused space for backup GPT; resizing is not attempted");
    byte[] entries=new byte[16384];Array.Copy(Basic.ToByteArray(),entries,16);Array.Copy(Guid.NewGuid().ToByteArray(),0,entries,16,16);
    ManagedExFat.Put64(entries,32,offset/sector);ManagedExFat.Put64(entries,40,(offset+length)/sector-1);
    byte[] name=Encoding.Unicode.GetBytes("NEXUS_USB");Array.Copy(name,0,entries,56,name.Length);
    Guid id=Guid.NewGuid();ulong end=sectors-1;
    changes.Add(new ConversionChange((end-entrySectors)*sector,entries));
    changes.Add(new ConversionChange(end*sector,MakeHeader(sector,sectors,end,1,end-entrySectors,id,entries)));
    changes.Add(new ConversionChange(2*sector,(byte[])entries.Clone()));
    changes.Add(new ConversionChange(sector,MakeHeader(sector,sectors,1,end,2,id,entries)));
    replacement[447]=0;replacement[448]=2;replacement[449]=0;replacement[450]=0xee;
    ManagedExFat.Put32(replacement,454,1);ManagedExFat.Put32(replacement,458,(uint)Math.Min(sectors-1,uint.MaxValue));
   } else {
    Need(disk.Length<=2000000000000UL&&offset/sector<=uint.MaxValue&&length/sector<=uint.MaxValue&&(offset+length)/sector<=1UL+uint.MaxValue,"MBR capacity limit; no truncation");
    // MSR carries no filesystem. Its reservation is dropped, its bytes untouched.
    changes.Add(new ConversionChange(sector,new byte[sector]));
    changes.Add(new ConversionChange(2*sector,new byte[16384]));
    changes.Add(new ConversionChange((sectors-1-entrySectors)*sector,new byte[16384]));
    changes.Add(new ConversionChange((sectors-1)*sector,new byte[sector]));
    replacement[450]=7;ManagedExFat.Put32(replacement,454,(uint)(offset/sector));ManagedExFat.Put32(replacement,458,(uint)(length/sector));
   }
   changes.Add(new ConversionChange(0,replacement));return changes;
  }
  static byte[] Header(IBlockDevice disk,ulong here,ulong other,ulong entrySectors) {
   byte[] b=disk.Read(here*disk.SectorSize,(int)disk.SectorSize);
   Need(Encoding.ASCII.GetString(b,0,8)=="EFI PART"&&U32(b,8)==0x10000&&U32(b,12)==92&&U32(b,20)==0,"GPT header format");
   uint checksum=U32(b,16);byte[] test=(byte[])b.Clone();ManagedExFat.Put32(test,16,0);
   Need(ManagedExFat.Crc(test,92)==checksum&&U64(b,24)==here&&U64(b,32)==other&&U32(b,80)==128&&U32(b,84)==128,"GPT header checksum/location");
   Need(U64(b,72)==(here==1?2:here-entrySectors)&&U64(b,40)>=2+entrySectors&&U64(b,48)<disk.Length/disk.SectorSize-1-entrySectors,"GPT usable bounds/table location");
   return b;
  }
  static byte[] MakeHeader(uint sector,ulong sectors,ulong here,ulong other,ulong table,Guid id,byte[] entries) {
   byte[] b=new byte[sector];Array.Copy(Encoding.ASCII.GetBytes("EFI PART"),b,8);ManagedExFat.Put32(b,8,0x10000);ManagedExFat.Put32(b,12,92);
   ManagedExFat.Put64(b,24,here);ManagedExFat.Put64(b,32,other);ManagedExFat.Put64(b,40,2+16384UL/sector);ManagedExFat.Put64(b,48,sectors-2-16384UL/sector);
   Array.Copy(id.ToByteArray(),0,b,56,16);ManagedExFat.Put64(b,72,table);ManagedExFat.Put32(b,80,128);ManagedExFat.Put32(b,84,128);ManagedExFat.Put32(b,88,ManagedExFat.Crc(entries,entries.Length));ManagedExFat.Put32(b,16,ManagedExFat.Crc(b,92));return b;
  }
 }
}
