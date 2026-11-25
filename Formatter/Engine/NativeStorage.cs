// Windows disk initialization only. No manual sector writes, clean or format.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NexusFormatter {
 internal interface IDiskIo : IDisposable { byte[] Call(uint code,byte[] input,int capacity); }
 public static partial class NativeStorage {
  internal const uint DeviceNumber=0x2d1080,LengthInfo=0x7405c,Geometry=0x700a0,
   StorageProperty=0x2d1400,Layout=0x70050,CreateDisk=0x7c058,UpdateProperties=0x70140;
  [StructLayout(LayoutKind.Sequential)] struct Luid {public uint Low;public int High;}
  [StructLayout(LayoutKind.Sequential)] struct TokenPrivilege {public uint Count;public Luid Id;public uint Attributes;}
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out SafeFileHandle token);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LookupPrivilegeValue(string system,string name,out Luid id);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(SafeFileHandle token,bool disable,ref TokenPrivilege state,uint length,IntPtr previous,IntPtr returned);
  public static void EnableDiskPrivilege() {
   SafeFileHandle token;
   if(!OpenProcessToken(GetCurrentProcess(),0x20|0x8,out token))throw Error("Open administrator token",Marshal.GetLastWin32Error());
   using(token) {
    Luid id;if(!LookupPrivilegeValue(null,"SeManageVolumePrivilege",out id))throw Error("Find disk-management privilege",Marshal.GetLastWin32Error());
    var state=new TokenPrivilege {Count=1,Id=id,Attributes=2};
    bool ok=AdjustTokenPrivileges(token,false,ref state,0,IntPtr.Zero,IntPtr.Zero);
    int error=Marshal.GetLastWin32Error();
    if(!ok||error!=0)throw Error("Enable disk-management privilege; run as administrator",error);
   }
  }
  static Win32Exception Error(string stage,int error) {
   return new Win32Exception(error,stage+" failed (Win32 "+error+"): "+new Win32Exception(error).Message);
  }
  internal static void ValidatePath(string path) {
   // A Get-Disk disk-interface path, NEVER a letter, volume path or caller-built PhysicalDrive number.
   if(path==null||path.Length>4096||!Regex.IsMatch(path,@"^\\\\\?\\[^\r\n]+#\{53f56307-b6bf-11d0-94f2-00a0c91efb8b\}\z",RegexOptions.IgnoreCase))
    throw new InvalidOperationException("Native disk access requires the verified Windows disk-interface path.");
  }
  public static string InitializeGpt(string path,uint number,ulong size,uint sector,string serial) {
   ValidatePath(path);
   using(var io=new DiskIo(path))return InitializeGpt(io,number,size,sector,serial);
  }
  static uint U32(byte[] b,int offset) {
   if(b==null||offset<0||b.Length-offset<4)throw new InvalidOperationException("Truncated Windows disk response.");
   return BitConverter.ToUInt32(b,offset);
  }
  static ulong U64(byte[] b,int offset) {
   if(b==null||offset<0||b.Length-offset<8)throw new InvalidOperationException("Truncated Windows disk response.");
   return BitConverter.ToUInt64(b,offset);
  }
  internal static byte[] GptRequest(Guid id) {
   if(id==Guid.Empty)throw new InvalidOperationException("Empty GPT disk ID.");
   byte[] b=new byte[24];Array.Copy(BitConverter.GetBytes(1u),0,b,0,4);
   Array.Copy(id.ToByteArray(),0,b,4,16);Array.Copy(BitConverter.GetBytes(128u),0,b,20,4);return b;
  }
  static Guid ReadGuid(byte[] b,int at) {
   if(b==null||at<0||b.Length-at<16)throw new InvalidOperationException("Truncated Windows GUID response.");
   byte[] id=new byte[16];Array.Copy(b,at,id,0,16);return new Guid(id);
  }
  static bool Zero(byte[] b,int at,int count) {
   for(int i=at;i<at+count;i++)if(b[i]!=0)return false;return true;
  }
  public sealed class LayoutState {
   internal bool CanCreate,AlreadyGpt;
   public string Summary;
   public string DiskId;
   public uint PartitionCount;
   public ulong ReservedOffset,ReservedSize;
  }
  internal static LayoutState ReadLayout(byte[] b,ulong size,uint sector) {
   // Windows x64 ABI: header48, PARTITION_INFORMATION_EX144, union offset32.
   // PartitionCount is an entry count: unused MBR slots still count.
   if(b==null||b.Length<48)throw new InvalidOperationException("Truncated native layout header; initialization refused.");
   uint style=U32(b,0),count=U32(b,4);
   string name=style==0?"MBR":style==1?"GPT":style==2?"RAW":"UNKNOWN";
   var text=new StringBuilder("Native layout: style="+name+" ("+style+"), entries="+count+", bytes="+b.Length);
   if(count>128||48UL+144UL*count>(ulong)b.Length)
    throw new InvalidOperationException(text+"; truncated/excessive partition entries; initialization refused.");
   bool empty=true,msrOnly=style==1&&count<=1,gptHeader=false;
   uint signature=style==0?U32(b,8):0;
   if(style==0)text.Append(", signature=0x"+signature.ToString("X8"));
   ulong usableStart=0,usableLength=0;
   if(style==1) {
    Guid id=ReadGuid(b,8);usableStart=U64(b,24);usableLength=U64(b,32);uint max=U32(b,40);
    text.Append(", diskId="+id+", usable="+usableStart+"+"+usableLength+", maxEntries="+max);
    gptHeader=id!=Guid.Empty&&max>=128&&count<=max&&usableStart>=2UL*sector&&usableStart<=1048576UL&&
     usableLength>0&&usableStart<size&&usableLength<=size-usableStart&&
     size-usableStart-usableLength>=2UL*sector&&usableStart%sector==0&&usableLength%sector==0;
   }
   for(int i=0;i<(int)count;i++) {
    int at=48+144*i;uint ps=U32(b,at),number=U32(b,at+24);
    ulong offset=U64(b,at+8),length=U64(b,at+16);
    text.Append("; entry"+i+" style="+ps+" number="+number+" offset="+offset+" length="+length+" service="+b[at+29]);
    bool unused=ps==style&&offset==0&&length==0&&number==0&&b[at+29]==0;
    if(style==0) {
     text.Append(" type=0x"+b[at+32].ToString("X2"));
     unused=unused&&b[at+32]==0&&b[at+33]==0&&b[at+34]==0&&U32(b,at+36)==0&&ReadGuid(b,at+40)==Guid.Empty;
    } else if(style==2)unused=unused&&Zero(b,at+32,112);
    else unused=false;
    empty=empty&&unused;
    if(style==1) {
     Guid type=ReadGuid(b,at+32),id=ReadGuid(b,at+48);ulong attributes=U64(b,at+64);
     text.Append(" type="+type+" id="+id+" attributes="+attributes);
     msrOnly=msrOnly&&ps==1&&number==1&&b[at+29]<=1&&id!=Guid.Empty&&attributes==0&&
      type==new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae")&&offset<=1048576UL&&offset%sector==0&&
      length>0&&length<=134217728UL&&length%sector==0&&offset>=usableStart&&
      offset-usableStart<=usableLength&&length<=usableLength-(offset-usableStart);
    }
   }
   // Permit only genuinely blank layouts after the provider reported RAW.
   // An unsigned MBR with four unused entries is not four data partitions.
   // Signed MBR, protective MBR, any data partition and malformed GPT still stop.
   bool blank=empty&&((style==2&&count==0)||(style==0&&signature==0&&(count==0||count==4)));
   bool existing=style==1&&gptHeader&&msrOnly;
   text.Append("; classification="+(blank?"blank":existing?"GPT-initialized-no-data":"REFUSED"));
   var state=new LayoutState{CanCreate=blank,AlreadyGpt=existing,Summary=text.ToString(),PartitionCount=count,
    DiskId=style==1?ReadGuid(b,8).ToString():"",ReservedOffset=existing&&count==1?U64(b,56):0,
    ReservedSize=existing&&count==1?U64(b,64):0};
   if(!blank&&!existing)throw new InvalidOperationException(state.Summary+". No partition table was written by native recovery.");
   return state;
  }
  static void ValidateDisk(IDiskIo io,uint number,ulong size,uint sector,string serial) {
   if(size<1073741824UL||size>140737488355328UL||(sector!=512&&sector!=4096)||size%sector!=0)
    throw new InvalidOperationException("Unsupported disk geometry.");
   byte[] device=io.Call(DeviceNumber,null,12);
   if(U32(device,0)!=7||U32(device,4)!=number||U32(device,8)!=0)
    throw new InvalidOperationException("Native handle is not the selected whole physical disk.");
   if(U64(io.Call(LengthInfo,null,8),0)!=size)throw new InvalidOperationException("Native disk capacity changed.");
   byte[] geometry=io.Call(Geometry,null,4096);
   if(U32(geometry,20)!=sector||U64(geometry,24)!=size)throw new InvalidOperationException("Native disk geometry changed.");
   // STORAGE_PROPERTY_QUERY includes AdditionalParameters[1] and padding.
   byte[] descriptor=io.Call(StorageProperty,new byte[12],4096);
   if(descriptor.Length<36||U32(descriptor,4)<36||U32(descriptor,4)>descriptor.Length||U32(descriptor,28)!=7)
    throw new InvalidOperationException("Native device is not a verified USB disk.");
   if(!String.IsNullOrWhiteSpace(serial)) {
    uint at=U32(descriptor,24);
    if(at<36||at>=U32(descriptor,4))throw new InvalidOperationException("Cannot verify native disk serial.");
    int end=Array.IndexOf(descriptor,(byte)0,(int)at,(int)U32(descriptor,4)-(int)at);
    if(end<0||!String.Equals(Encoding.ASCII.GetString(descriptor,(int)at,end-(int)at).Trim(),serial.Trim(),StringComparison.Ordinal))
     throw new InvalidOperationException("Native disk serial changed; no further writes are permitted.");
   }
  }
  // Read-only post-initialization check: no create, refresh, erase or format IOCTL.
  public static LayoutState ReadGpt(string path,uint number,ulong size,uint sector,string serial) {
   ValidatePath(path);
   using(var io=new DiskIo(path,false))return ReadGpt(io,number,size,sector,serial);
  }
  internal static LayoutState ReadGpt(IDiskIo io,uint number,ulong size,uint sector,string serial) {
   ValidateDisk(io,number,size,sector,serial);
   var state=ReadLayout(io.Call(Layout,null,65536),size,sector);
   if(!state.AlreadyGpt)throw new InvalidOperationException(state.Summary+". Expected initialized GPT; no writes attempted.");
   return state;
  }
  internal static string InitializeGpt(IDiskIo io,uint number,ulong size,uint sector,string serial) {
   ValidateDisk(io,number,size,sector,serial);
   // Called only after provider RAW + zero partitions, with the same identity.
   // Reconcile the native view; never overwrite an already-created GPT.
   var first=ReadLayout(io.Call(Layout,null,65536),size,sector);
   var second=ReadLayout(io.Call(Layout,null,65536),size,sector);
   if(first.Summary!=second.Summary)throw new InvalidOperationException("Native layout changed during recovery. "+first.Summary+" -> "+second.Summary+". No native write attempted.");
   try {
    if(second.CanCreate)io.Call(CreateDisk,GptRequest(Guid.NewGuid()),0);
    io.Call(UpdateProperties,null,0);
   } catch(Exception e) {throw new InvalidOperationException(second.Summary+". Native GPT recovery stopped without a retry: "+e.Message,e);}
   return second.Summary+(second.AlreadyGpt?". Existing GPT preserved; no second initialization.":". One native GPT initialization issued.");
  }
  sealed class DiskIo : IDiskIo {
   readonly SafeFileHandle handle;
   [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
   [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inSize,[Out] byte[] output,int outSize,out int returned,IntPtr overlapped);
   public DiskIo(string path,bool writable=true) {
    handle=CreateFile(path,writable?0xc0000000u:0x80000000u,3,IntPtr.Zero,3,0,IntPtr.Zero);
    if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw Error("Open selected disk",error);}
   }
   public byte[] Call(uint code,byte[] input,int capacity) {
    byte[] output=capacity==0?null:new byte[capacity];int returned;
    if(!DeviceIoControl(handle,code,input,input==null?0:input.Length,output,capacity,out returned,IntPtr.Zero))
     throw Error("Disk IOCTL 0x"+code.ToString("X8"),Marshal.GetLastWin32Error());
    if(returned<0||returned>capacity)throw new InvalidOperationException("Invalid Windows disk response size.");
    if(output==null)return new byte[0];Array.Resize(ref output,returned);return output;
   }
   public void Dispose(){handle.Dispose();}
  }
 }
}
