// Guarded Windows partition fallback. Never raw-sector writes or a second erase.
using System;
using System.ComponentModel;
using System.Text;

namespace NexusFormatter {
 public static partial class NativeStorage {
  internal const uint IsWritable=0x70024,SetLayout=0x7c054;
  const ulong MiB=1048576;
  static readonly Guid DataType=new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
  public sealed class PartitionPlan {
   public uint DiskNumber,PartitionNumber;
   public ulong Offset,Size;
   public string Style,Summary;
   internal byte[] Bytes;
  }
  static void Put32(byte[] b,int at,uint value){Array.Copy(BitConverter.GetBytes(value),0,b,at,4);}
  static void Put64(byte[] b,int at,ulong value){Array.Copy(BitConverter.GetBytes(value),0,b,at,8);}
  static bool Same(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  public static string CheckWriteAccess(string path,uint number,ulong size,uint sector,string serial) {
   ValidatePath(path);
   // Opening a read/write handle and querying writability does NOT write data.
   using(var io=new DiskIo(path))return CheckWriteAccess(io,number,size,sector,serial);
  }
  internal static string CheckWriteAccess(IDiskIo io,uint number,ulong size,uint sector,string serial) {
   ValidateDisk(io,number,size,sector,serial);io.Call(IsWritable,null,0);
   return "Verified USB identity, read/write handle and Windows writable status. No disk data written by preflight.";
  }
  public static string InitializeMbr(string path,uint number,ulong size,uint sector,string serial) {
   ValidatePath(path);using(var io=new DiskIo(path))return InitializeMbr(io,number,size,sector,serial);
  }
  internal static string InitializeMbr(IDiskIo io,uint number,ulong size,uint sector,string serial) {
   ValidateDisk(io,number,size,sector,serial);
   if(size>2000000000000UL)throw new InvalidOperationException("MBR policy limit exceeded.");
   var a=io.Call(Layout,null,65536);var b=io.Call(Layout,null,65536);
   if(!Same(a,b))throw new InvalidOperationException("Native layout changed; MBR recovery refused.");
   var state=ReadLayout(b,size,sector);
   if(!state.CanCreate)throw new InvalidOperationException("MBR recovery requires a verified blank disk.");
   var request=new byte[24];uint signature=BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(),0);
   if(signature==0)signature=1;Put32(request,4,signature);
   io.Call(CreateDisk,request,0);io.Call(UpdateProperties,null,0);
   return "One native MBR initialization issued; provider readback required.";
  }
  internal static PartitionPlan PlanPartition(byte[] before,uint number,ulong diskSize,uint sector,string style,bool msrOnly,ulong reservedOffset,ulong reservedSize) {
   if((sector!=512&&sector!=4096)||diskSize%sector!=0||diskSize<1073741824UL||diskSize>140737488355328UL)
    throw new InvalidOperationException("Unsupported partition geometry.");
   if(before==null||before.Length<48)throw new InvalidOperationException("Truncated partition header.");
   uint ps=U32(before,0),count=U32(before,4);ulong offset=MiB,end=diskSize;
   uint next=1;byte[] output;
   if(style=="GPT"&&ps==1) {
    var state=ReadLayout(before,diskSize,sector);
    if(!state.AlreadyGpt||state.ReservedOffset!=reservedOffset||state.ReservedSize!=reservedSize)
     throw new InvalidOperationException("Provider/native reserved partition disagreement.");
    // Microsoft requires MSR arrival before SET_DRIVE_LAYOUT_EX. Never use
    // this fallback to manufacture an MSR on an empty GPT disk.
    if(msrOnly||count!=1)throw new InvalidOperationException("Wait for verified MSR before creating GPT data.");
    end=U64(before,24)+U64(before,32);next=count+1;
    if(!msrOnly)offset=((reservedOffset+reservedSize+MiB-1)/MiB)*MiB;
    output=new byte[48+144*(count+1)];Array.Copy(before,output,48+144*(int)count);Put32(output,4,count+1);
   } else if(style=="MBR"&&ps==0&&!msrOnly&&reservedOffset==0&&reservedSize==0&&diskSize<=2000000000000UL) {
    // Signed but completely empty MBR is expected AFTER Initialize-Disk.
    // Use the strict empty-slot parser without treating the disk signature as data.
    var blank=(byte[])before.Clone();Put32(blank,8,0);
    if(U32(before,8)==0||!ReadLayout(blank,diskSize,sector).CanCreate)
     throw new InvalidOperationException("Expected initialized, empty MBR.");
    output=new byte[48+4*144];Array.Copy(before,output,48);Put32(output,4,4);
   } else throw new InvalidOperationException("Unexpected partition style or unsafe layout; no native write.");
   if(end<=offset||(end/MiB)*MiB<=offset)throw new InvalidOperationException("No usable partition extent.");
   ulong length=(end/MiB)*MiB-offset;
   // A supported 1 GiB disk loses space to alignment and the GPT MSR.
   // Do not mistakenly require its data partition to also be >= 1 GiB.
   if(end<=offset||length>end-offset||length<536870912UL||offset%MiB!=0||length%sector!=0)
    throw new InvalidOperationException("Partition extent is invalid.");
   int at=48+(int)(next-1)*144;Put32(output,at,ps);Put64(output,at+8,offset);Put64(output,at+16,length);
   Put32(output,at+24,next);output[at+28]=1;
   if(ps==1) {
    Array.Copy(DataType.ToByteArray(),0,output,at+32,16);
    Array.Copy(Guid.NewGuid().ToByteArray(),0,output,at+48,16);
    Array.Copy(Encoding.Unicode.GetBytes("NEXUS_USB"),0,output,at+72,18);
   } else {
    output[at+32]=7;output[at+34]=1;Put32(output,at+36,(uint)(offset/sector));
    for(int i=1;i<4;i++)output[48+i*144+28]=1;
   }
   return new PartitionPlan{DiskNumber=number,PartitionNumber=next,Offset=offset,Size=length,Style=style,Bytes=output,
    Summary="Native "+style+" "+(msrOnly?"reserved":"data")+" partition "+next+" at "+offset+" size "+length};
  }
  internal static bool MatchesPartition(byte[] actual,PartitionPlan plan) {
   var wanted=plan.Bytes;
   if(actual==null||actual.Length<48||U32(actual,0)!=U32(wanted,0)||U32(actual,4)!=U32(wanted,4))return false;
   uint count=U32(wanted,4);if(actual.Length<48+144*count)return false;
   bool gpt=U32(wanted,0)==1;
   // Windows may change padding, partition ordinal and RewritePartition.
   if(gpt) {for(int i=8;i<44;i++)if(actual[i]!=wanted[i])return false;}
   else if(U32(actual,8)!=U32(wanted,8))return false;
   for(int n=0;n<count;n++) {
    int at=48+144*n;
    if(U32(actual,at)!=U32(wanted,at)||U64(actual,at+8)!=U64(wanted,at+8)||U64(actual,at+16)!=U64(wanted,at+16)||
       U32(actual,at+24)!=U32(wanted,at+24))return false;
    if(gpt) {for(int i=32;i<144;i++)if(actual[at+i]!=wanted[at+i])return false;}
    else if(actual[at+32]!=wanted[at+32]||actual[at+33]!=wanted[at+33]||U32(actual,at+36)!=U32(wanted,at+36))return false;
   }
   return true;
  }
  public static PartitionPlan AppendPartition(string path,uint number,ulong size,uint sector,string serial,string style,bool msrOnly,ulong reservedOffset,ulong reservedSize) {
   ValidatePath(path);using(var io=new DiskIo(path))return AppendPartition(io,number,size,sector,serial,style,msrOnly,reservedOffset,reservedSize);
  }
  internal static PartitionPlan AppendPartition(IDiskIo io,uint number,ulong size,uint sector,string serial,string style,bool msrOnly,ulong reservedOffset,ulong reservedSize) {
   ValidateDisk(io,number,size,sector,serial);io.Call(IsWritable,null,0);
   var first=io.Call(Layout,null,65536);var second=io.Call(Layout,null,65536);
   if(!Same(first,second))throw new InvalidOperationException("Native layout changed before partition creation; no write.");
   var plan=PlanPartition(second,number,size,sector,style,msrOnly,reservedOffset,reservedSize);
   // One operation only. A failure may mean partial success; adopt only exact
   // readback and NEVER issue SetLayout a second time or reinitialize the disk.
   Exception failure=null;
   try {io.Call(SetLayout,plan.Bytes,0);}catch(Win32Exception e){failure=e;}
   io.Call(UpdateProperties,null,0);
   ValidateDisk(io,number,size,sector,serial);
   if(!MatchesPartition(io.Call(Layout,null,65536),plan))
    throw new InvalidOperationException("Native partition write did not verify. No retry. "+(failure==null?"":failure.Message),failure);
   plan.Summary+="; exact native readback verified"+(failure==null?"":" after Windows reported "+failure.Message);
   return plan;
  }
 }
}
