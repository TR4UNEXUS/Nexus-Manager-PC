using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
namespace NexusFormatter {
 public static partial class NativeStorage {
  sealed class ConversionDevice : IBlockDevice {
   readonly IFormatSession session;readonly Action<string> log;DateTime last=DateTime.UtcNow;ulong bytes;
   internal ConversionDevice(IFormatSession session,Action<string> log){this.session=session;this.log=log;}
   public ulong Length{get{return session.Length;}}public uint SectorSize{get{return session.SectorSize;}}
   public byte[] Read(ulong at,int size){session.Check();byte[] b=session.Read(at,size);Progress(size);return b;}
   public void Write(ulong at,byte[] b){session.Check();session.Write(at,b);Progress(b.Length);}
   public void Flush(){session.Check();session.Flush();}
   void Progress(int count){bytes+=(uint)count;if((DateTime.UtcNow-last).TotalSeconds>=2){log("Working / "+(bytes/1048576)+" MB processed. Keep drive connected.");last=DateTime.UtcNow;}}
  }
  static IEnumerable<ConversionChange> ConversionChanges(ExFatConversion exfat,bool clusters,List<ConversionChange> table) {
   if(clusters)foreach(var c in exfat.Recluster512())yield return c;
   foreach(var c in table)yield return c;
  }
  static bool SameManifest(byte[] a,byte[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  public static string ConvertOwned(string path,uint number,ulong size,uint sector,string serial,string oldStyle,
   uint[] numbers,ulong[] offsets,ulong[] sizes,ulong selectedOffset,ulong selectedSize,string targetStyle,
   bool clusters,string journalPath,Action<string> log,Action writesStarted) {
   ValidatePath(path);if(String.IsNullOrWhiteSpace(serial)||log==null||writesStarted==null)throw new InvalidOperationException("Conversion identity/callback missing.");
   var session=new FormatSession(new FormatPlatform(),path,number,size,sector,serial,oldStyle,numbers,offsets,sizes,log,true,false);
   Exception failure=null;string result=null;
   try {
    session.Acquire();var device=new ConversionDevice(session,log);
    log("Checking partition tables and every allocation chain. No disk writes yet.");
    var table=PartitionConversion.Plan(device,oldStyle,targetStyle,selectedOffset,selectedSize);
    var exfat=new ExFatConversion(device,selectedOffset,selectedSize);
    bool changeClusters=clusters&&exfat.ClusterBytes!=524288;
    if(changeClusters&&exfat.ClusterBytes>524288)throw new IOException("Reducing clusters larger than 512 KB is not supported; no changes.");
    if(!changeClusters&&oldStyle==targetStyle)result="Already matches the selected settings. No changes.";
    else {
     byte[] manifest=null;
     if(changeClusters){log("Hashing file contents before conversion.");manifest=exfat.FileManifest();}
     log("Saving original and converted ranges to PC recovery journal: "+journalPath);
     using(var journal=new ConversionJournal(device,journalPath,ConversionJournal.Binding(path,serial,size,sector),false)) {
      journal.Prepare(ConversionChanges(exfat,changeClusters,table),session.Check);
      log("Validating staged filesystem and file hashes before touching USB.");
      PartitionConversion.Plan(journal,targetStyle,targetStyle,selectedOffset,selectedSize);
      if(changeClusters) {
       var staged=new ExFatConversion(journal,selectedOffset,selectedSize);
       if(staged.ClusterBytes!=524288||!SameManifest(manifest,staged.FileManifest()))throw new IOException("Staged files did not match originals. No disk writes.");
      }
      log("Applying verified conversion. Do not unplug or close the app.");
      journal.Apply(session.Check,delegate{session.KeepOfflineOnRelease=true;writesStarted();});
      PartitionConversion.Plan(device,targetStyle,targetStyle,selectedOffset,selectedSize);
      if(changeClusters&&!SameManifest(manifest,new ExFatConversion(device,selectedOffset,selectedSize).FileManifest()))throw new IOException("Final file hash mismatch. Recovery required.");
      session.KeepOfflineOnRelease=false;
     }
     result="Converted / "+targetStyle+(changeClusters?" / 512 KB":"")+" / files preserved and changed ranges verified.";
    }
   } catch(Exception e){failure=e;}
   try{session.Release();}catch(Exception e){failure=failure==null?e:new AggregateException(failure,e);}
   finally{session.Dispose();}
   if(failure!=null)throw new IOException("Conversion stopped. Keep recovery file: "+journalPath+". "+failure.Message,failure);
   return result;
  }
  public static string RecoverConversion(string path,uint number,ulong size,uint sector,string serial,string style,
   uint[] numbers,ulong[] offsets,ulong[] sizes,string journalPath,Action<string> log,Action writesStarted) {
   ValidatePath(path);if(String.IsNullOrWhiteSpace(serial)||log==null||writesStarted==null)throw new InvalidOperationException("Recovery identity/callback missing.");
   var session=new FormatSession(new FormatPlatform(),path,number,size,sector,serial,style,numbers,offsets,sizes,log,true,true);
   Exception failure=null;
   try {
    session.Acquire();var device=new ConversionDevice(session,log);
    using(var journal=new ConversionJournal(device,journalPath,ConversionJournal.Binding(path,serial,size,sector),true)) {
     log("Verifying recovery journal for this exact drive.");
     journal.Restore(session.Check,writesStarted);session.KeepOfflineOnRelease=false;
    }
   }catch(Exception e){failure=e;}
   try{session.Release();}catch(Exception e){failure=failure==null?e:new AggregateException(failure,e);}
   finally{session.Dispose();}
   if(failure!=null)throw new IOException("Recovery stopped; keep the disk offline and retain the journal. "+failure.Message,failure);
   return "Original ranges restored and read back. Safely reconnect, then check the drive.";
  }
 }
}
