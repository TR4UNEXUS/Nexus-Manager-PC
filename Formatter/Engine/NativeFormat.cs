// The sole live erase path. Win32 is used for device access/locking only;
// partition and filesystem bytes are produced by ManagedExFat, not Windows formatters.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NexusFormatter {
 internal interface IRawFormatHandle : IDiskIo {
  void Transfer(ulong offset,byte[] data,bool write);
  void Flush();
 }
 internal interface IFormatPlatform {
  IRawFormatHandle Open(string path,uint access,bool raw);
  List<string> Volumes();
  void Pause();
 }
 internal interface IFormatSession : IBlockDevice,IDisposable {
  void Acquire();
  void Check();
  void Release();
 }
 public static partial class NativeStorage {
  internal const uint GetAttributes=0x700f0,SetAttributes=0x7c0f4,VolumeExtents=0x560000,
   LockVolume=0x90018,UnlockVolume=0x9001c,DismountVolume=0x90020;
  // ABI is 40 bytes, despite the incorrect sizeof(GET_DISK_ATTRIBUTES) text
  // in one version of the docs. Native SDK layout tested in LayoutAbi.c.
  internal static byte[] OfflineRequest(bool offline,bool persist=false) {
   byte[] b=new byte[40];Put32(b,0,40);b[4]=(byte)(persist?1:0);Put64(b,8,offline?1UL:0UL);Put64(b,16,1);return b;
  }
  internal static void RunOwnedFormat(IFormatSession session,ExFatPlan plan,Action<string> log) {
   Exception failure=null;
   try {session.Acquire();session.Check();ManagedExFat.Format(session,plan,session.Check,log);}
   catch(Exception e){failure=e;}
   try {session.Release();}catch(Exception e) {
    failure=failure==null?e:new AggregateException("Format failed; cleanup also needs attention. "+failure.Message+" / "+e.Message,failure,e);
   }
   finally {session.Dispose();}
   if(failure!=null)throw new InvalidOperationException("Native formatting stopped; no automatic retry. "+failure.Message,failure);
  }
  // Called only after the UI request, protected disks/folders and layout checks.
  // Native code independently pins the same physical handle and verifies extents.
  public static string FormatOwned(string path,uint number,ulong size,uint sector,string serial,string oldStyle,
    uint[] partNumbers,ulong[] offsets,ulong[] sizes,string style,string label,Action<string> log) {
   ValidatePath(path);
   if(log==null)throw new ArgumentNullException("log");
   var id=Guid.NewGuid();uint volumeSerial=BitConverter.ToUInt32(id.ToByteArray(),0);if(volumeSerial==0)volumeSerial=1;
   var plan=ManagedExFat.Plan(size,sector,style,label,id,Guid.NewGuid(),volumeSerial);
   if(String.IsNullOrWhiteSpace(serial))throw new InvalidOperationException("Native formatting requires a readable hardware serial to pin drive identity; no erase.");
   RunOwnedFormat(new FormatSession(new FormatPlatform(),path,number,size,sector,serial,oldStyle,partNumbers,offsets,sizes,log),plan,log);
   return "Nexus native metadata verified: "+style+" / exFAT / 512 KiB. Disk locks released. No Windows formatting provider used.";
  }
  internal static void MatchSelectedLayout(byte[] b,string style,uint[] numbers,ulong[] offsets,ulong[] sizes,ulong diskSize,uint sector) {
   uint wanted=style=="RAW"?2u:style=="MBR"?0u:style=="GPT"?1u:99u;
   if(numbers==null||offsets==null||sizes==null||numbers.Length!=offsets.Length||numbers.Length!=sizes.Length||numbers.Length>128||
      b==null||b.Length<48||wanted==99||U32(b,4)>128||48UL+144UL*U32(b,4)>(ulong)b.Length)
    throw new InvalidOperationException("Native selected partition layout could not be verified; no erase.");
   // Some USB bridges expose an unsigned empty MBR while Storage reports RAW.
   // Reuse the strict blank classifier; never adopt signed/protective/data MBRs.
   if(wanted==2) {
    if(numbers.Length!=0||!ReadLayout(b,diskSize,sector).CanCreate)
     throw new InvalidOperationException("Selected RAW disk is not natively blank; no erase.");
    return;
   }
   if(U32(b,0)!=wanted)throw new InvalidOperationException("Native partition style differs from selection; no erase.");
   var seen=new HashSet<uint>();int active=0;
   for(int n=0;n<U32(b,4);n++) {
    int at=48+n*144;ulong length=U64(b,at+16);if(length==0)continue;
    uint num=U32(b,at+24);int ix=Array.IndexOf(numbers,num);
    if(U32(b,at)!=wanted||!seen.Add(num)||ix<0||U64(b,at+8)!=offsets[ix]||length!=sizes[ix])
     throw new InvalidOperationException("Native partition extents changed after selection; no erase.");
    active++;
   }
   if(active!=numbers.Length||(wanted==2&&active!=0))throw new InvalidOperationException("Native partition inventory differs from selection; no erase.");
  }
  internal sealed class FormatSession : IFormatSession {
   readonly string path,serial,oldStyle;readonly uint number,sector;readonly ulong size;
   readonly uint[] numbers;readonly ulong[] offsets,sizes;readonly Action<string> log;
   readonly IFormatPlatform platform;IRawFormatHandle disk;readonly List<VolumeLock> locks=new List<VolumeLock>();
   byte[] layout,descriptor;bool offlineAttempted,acquired,released;readonly bool conversion,recovery;
   internal bool KeepOfflineOnRelease;
   internal FormatSession(IFormatPlatform platform,string path,uint number,ulong size,uint sector,string serial,string oldStyle,uint[] numbers,ulong[] offsets,ulong[] sizes,Action<string> log,bool conversion=false,bool recovery=false) {
    this.platform=platform;
    this.path=path;this.number=number;this.size=size;this.sector=sector;this.serial=serial;this.oldStyle=oldStyle;
    this.numbers=numbers;this.offsets=offsets;this.sizes=sizes;this.log=log;this.conversion=conversion;this.recovery=recovery;
   }
   public ulong Length {get{return size;}}
   public uint SectorSize {get{return sector;}}
   public void Acquire() {
    if(disk!=null||released)throw new InvalidOperationException("Format session cannot be reused.");
    log("Opening the selected disk read/write; checking identity and write protection (no erase yet).");
    disk=platform.Open(path,0xc0000000,true);
    ValidateDisk(disk,number,size,sector,serial);disk.Call(IsWritable,null,0);
    descriptor=disk.Call(StorageProperty,new byte[12],4096);
    ulong initial=U64(disk.Call(GetAttributes,null,16),8)&3;
    if((initial&2)!=0||(!recovery&&initial!=0))throw new InvalidOperationException("Selected disk is already offline or read-only; no override.");
    if(recovery){if(initial!=1)throw new InvalidOperationException("Recovery requires the interrupted disk to remain offline.");KeepOfflineOnRelease=true;}
    layout=disk.Call(Layout,null,65536);if(!recovery)MatchSelectedLayout(layout,oldStyle,numbers,offsets,sizes,size,sector);
    List<string> names=platform.Volumes();
    foreach(string name in names) {
     byte[] extents;
     using(var probe=platform.Open(name.TrimEnd('\\'),0,false)) {
      try {extents=probe.Call(VolumeExtents,null,65536);}
      catch(Win32Exception e){if(e.NativeErrorCode==1||e.NativeErrorCode==50)continue;throw;}
     }
     if(!TargetsDisk(extents,number,size))continue;
     log("Acquiring exclusive volume lock: "+name);
     var volume=new VolumeLock(platform,name,number,size,extents);locks.Add(volume);volume.Acquire();
    }
    // Detect arrival/removal of any volume while acquiring locks. No erase yet.
    if(String.Join("|",names.ToArray())!=String.Join("|",platform.Volumes().ToArray()))throw new IOException("Volume topology changed while locking. Refresh and reselect.");
    ValidateDisk(disk,number,size,sector,serial);
    if(!Same(layout,disk.Call(Layout,null,65536)))throw new IOException("Disk layout changed while locking; no erase.");
    foreach(var volume in locks)volume.Dismount(); // Never force-dismount a volume we failed to lock.
    // Offline is temporary, restricted to this disk and never changes read-only.
    // This prevents Windows mounting newly published partitions mid-operation.
    log("Holding all volume locks; temporarily taking only the selected disk offline to prevent remounts.");
    // Conversion must survive a process/PC interruption offline on this host.
    // Clear the persistent attribute only after verification or safe preflight refusal.
    offlineAttempted=true;disk.Call(SetAttributes,OfflineRequest(true,conversion),0);
    if((U64(disk.Call(GetAttributes,null,16),8)&3)!=1)throw new IOException("Could not verify isolated writable disk; no erase.");
    if(!recovery)MatchSelectedLayout(disk.Call(Layout,null,65536),oldStyle,numbers,offsets,sizes,size,sector);
    acquired=true;Check();log(conversion?"Exclusive conversion session acquired. No format or erase fallback.":"Exclusive formatting session acquired. All partitions on the selected disk will now be erased.");
   }
   public void Check() {
    if(!acquired||released||disk==null)throw new IOException("Exclusive format session is not held.");
    ValidateDisk(disk,number,size,sector,serial);
    if(!Same(descriptor,disk.Call(StorageProperty,new byte[12],4096)))throw new IOException("Device descriptor changed; no further writes.");
    if((U64(disk.Call(GetAttributes,null,16),8)&3)!=1)throw new IOException("Disk isolation/write state changed; no further writes.");
    disk.Call(IsWritable,null,0);
   }
   void Bounds(ulong offset,int count) {
    if(!acquired||released||count<=0||offset%sector!=0||count%sector!=0||offset>size||(ulong)count>size-offset)
     throw new IOException("Invalid or unlocked raw I/O request.");
   }
   public void Write(ulong offset,byte[] bytes){Bounds(offset,bytes.Length);disk.Transfer(offset,bytes,true);}
   public byte[] Read(ulong offset,int count){Bounds(offset,count);byte[] b=new byte[count];disk.Transfer(offset,b,false);return b;}
   public void Flush(){if(!acquired||released)throw new IOException("Flush outside lock lease.");disk.Flush();}
   public void Release() {
    if(released)return;released=true;Exception cleanup=null;
    // Flush failures must not suppress release of any acquired handles.
    if(acquired)try {disk.Flush();}catch(Exception e){cleanup=e;if(conversion)KeepOfflineOnRelease=true;}
    for(int i=locks.Count-1;i>=0;i--)try{locks[i].Dispose();}catch(Exception e){cleanup=cleanup??e;}
    locks.Clear();
    if(offlineAttempted&&disk!=null&&!KeepOfflineOnRelease)try {
     // Same handle only: NEVER reopen by a reused disk number during cleanup.
     ValidateDisk(disk,number,size,sector,serial);
     try {disk.Call(UpdateProperties,null,0);}catch(Exception e){if(conversion){KeepOfflineOnRelease=true;throw;}cleanup=cleanup??e;}
     disk.Call(SetAttributes,OfflineRequest(false,conversion),0);
     if((U64(disk.Call(GetAttributes,null,16),8)&1)!=0)throw new IOException("Disk remains offline after release.");
     disk.Call(UpdateProperties,null,0);
    }catch(Exception e){cleanup=cleanup??e;}
    if(disk!=null){disk.Dispose();disk=null;}
    if(cleanup!=null)throw new IOException("Unable to complete cleanup/remount: "+cleanup.Message+". Safely reconnect the same drive; do not blindly reformat.",cleanup);
    log(KeepOfflineOnRelease?"Disk left OFFLINE after interrupted conversion. Keep the PC recovery journal; do not format or bring online manually.":"Volume locks and raw handle released; temporary offline state restored.");
   }
   public void Dispose(){if(!released)Release();if(disk!=null)disk.Dispose();}
  }
  internal static bool TargetsDisk(byte[] b,uint number,ulong size) {
   uint count=U32(b,0);if(count==0||count>128||8UL+24UL*count>(ulong)b.Length)throw new IOException("Malformed volume extents; cannot prove target ownership.");
   bool match=false;
   for(int i=0;i<count;i++)if(U32(b,8+24*i)==number) {
    ulong start=U64(b,16+24*i),length=U64(b,24+24*i);
    if(count!=1||length==0||start>=size||length>size-start)throw new IOException("Spanned or invalid target volume; no erase.");match=true;
   }
   return match;
  }
  sealed class VolumeLock : IDisposable {
   readonly IFormatPlatform platform;readonly string name;readonly uint number;readonly ulong size;readonly byte[] original;IRawFormatHandle handle;bool locked;
   internal VolumeLock(IFormatPlatform platform,string name,uint number,ulong size,byte[] original){this.platform=platform;this.name=name;this.number=number;this.size=size;this.original=original;}
   public void Acquire() {
    handle=platform.Open(name.TrimEnd('\\'),0xc0000000,false);
    if(!Same(original,handle.Call(VolumeExtents,null,65536))||!TargetsDisk(original,number,size))throw new IOException("Volume moved before lock.");
    for(int attempt=0;;attempt++)try {handle.Call(LockVolume,null,0);locked=true;break;}
     catch(Win32Exception e){if(attempt==2||(e.NativeErrorCode!=5&&e.NativeErrorCode!=32&&e.NativeErrorCode!=33))throw new IOException("Could not exclusively lock "+name+". Close Explorer/apps using it; nothing was erased. "+e.Message,e);platform.Pause();}
   }
   public void Dismount(){if(!locked)throw new IOException("Refusing to dismount an unlocked volume.");handle.Call(DismountVolume,null,0);}
   // Closing the owning handle releases FSCTL_LOCK_VOLUME even after offlining.
   public void Dispose() {if(handle==null)return;handle.Dispose();handle=null;locked=false;}
  }
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr FindFirstVolume(StringBuilder name,uint length);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool FindNextVolume(IntPtr handle,StringBuilder name,uint length);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool FindVolumeClose(IntPtr handle);
  static List<string> VolumeNames() {
   var list=new List<string>();var buffer=new StringBuilder(1024);IntPtr h=FindFirstVolume(buffer,1024);
   if(h==new IntPtr(-1)) {int e=Marshal.GetLastWin32Error();if(e==18)return list;throw Error("Enumerate volumes before locking",e);}
   try {for(;;) {
    string name=buffer.ToString();if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\\z"))throw new IOException("Invalid volume GUID path.");
    list.Add(name);if(list.Count>1024)throw new IOException("Excessive volume inventory.");
    if(!FindNextVolume(h,buffer,1024)){int e=Marshal.GetLastWin32Error();if(e!=18)throw Error("Enumerate next volume",e);break;}
   }} finally {FindVolumeClose(h);}
   list.Sort(StringComparer.OrdinalIgnoreCase);return list;
  }
  sealed class FormatPlatform : IFormatPlatform {
   public IRawFormatHandle Open(string path,uint access,bool raw){return new RawHandle(path,access,raw);}
   public List<string> Volumes(){return VolumeNames();}
   public void Pause(){Thread.Sleep(150);}
  }
  sealed class RawHandle : IRawFormatHandle {
   readonly SafeFileHandle handle;
   [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
   [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inSize,[Out] byte[] output,int outSize,out int returned,IntPtr overlapped);
   [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFilePointerEx(SafeFileHandle handle,long offset,out long actual,uint origin);
   [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadFile(SafeFileHandle h,IntPtr data,int count,out int done,IntPtr overlapped);
   [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteFile(SafeFileHandle h,IntPtr data,int count,out int done,IntPtr overlapped);
   [DllImport("kernel32.dll",SetLastError=true)] static extern bool FlushFileBuffers(SafeFileHandle h);
   internal RawHandle(string path,uint access,bool raw) {
    handle=CreateFile(path,access,3,IntPtr.Zero,3,raw?0xa0000000u:0,IntPtr.Zero);
    if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw Error("Open device "+path,error);}
   }
   public byte[] Call(uint code,byte[] input,int capacity) {
    byte[] output=capacity==0?null:new byte[capacity];int done;
    if(!DeviceIoControl(handle,code,input,input==null?0:input.Length,output,capacity,out done,IntPtr.Zero))throw Error("Native IOCTL 0x"+code.ToString("X"),Marshal.GetLastWin32Error());
    if(done<0||done>capacity)throw new IOException("Invalid native response size.");
    if(output==null)return new byte[0];Array.Resize(ref output,done);return output;
   }
   public void Transfer(ulong offset,byte[] data,bool write) {
    // 64 KiB-aligned buffer handles 512e/4Kn noncached device I/O. Never partial retry.
    IntPtr allocation=Marshal.AllocHGlobal(checked(data.Length+65535));
    IntPtr aligned=new IntPtr((allocation.ToInt64()+65535)&~65535L);
    try {
     long position;if(!SetFilePointerEx(handle,checked((long)offset),out position,0))throw Error("Seek raw disk",Marshal.GetLastWin32Error());
     if(position!=(long)offset)throw new IOException("Raw seek mismatch.");int done;
     if(write)Marshal.Copy(data,0,aligned,data.Length);
     bool ok=write?WriteFile(handle,aligned,data.Length,out done,IntPtr.Zero):ReadFile(handle,aligned,data.Length,out done,IntPtr.Zero);
     if(!ok)throw Error((write?"Write":"Read")+" raw disk byte "+offset,Marshal.GetLastWin32Error());
     if(done!=data.Length)throw new IOException("Short raw "+(write?"write":"read")+" at "+offset+"; no retry.");
     if(!write)Marshal.Copy(aligned,data,0,data.Length);
    } finally {Marshal.FreeHGlobal(allocation);}
   }
   public void Flush(){if(!FlushFileBuffers(handle))throw Error("Flush raw disk",Marshal.GetLastWin32Error());}
   public void Dispose(){handle.Dispose();}
  }
 }
}
