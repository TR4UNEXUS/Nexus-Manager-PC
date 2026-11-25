using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
namespace NexusFormatter {
 public sealed partial class Program {
  void SelectDetectedDrive() {
   // Never guess between eligible drives, or retarget an armed erase operation.
   drives.SelectedIndex=-1;if(!keepFiles||busy)return;
   int chosen=-1;
   for(int i=0;i<drives.Items.Count;i++) {
    var item=drives.Items[i] as DiskItem;
    if(item==null||!Convert.ToBoolean(item.Data["allowed"])||Convert.ToDecimal(item.Data["size"])>140737488355328m)continue;
    if(chosen!=-1){stage.Text="Several USB drives found / choose one.";return;}chosen=i;
   }
   if(chosen>=0){drives.SelectedIndex=chosen;stage.Text="USB drive detected / ready.";}
   else stage.Text=drives.Items.Count==0?"No USB drives found.":"No eligible drive / select one to see details.";
  }
  async Task ConvertPartition(bool recovery) {
   if(busy||!loggingHealthy)return;
   var selected=drives.SelectedItem as DiskItem;var partition=partitions.SelectedItem as PartitionItem;
   if(selected==null||(!recovery&&(!keepFiles||!ready||partition==null)))return;
   var d=selected.Data;
   string oldStyle=Convert.ToString(d["style"]);
   int tableIndex=Find<ComboBox>("ConversionTarget").SelectedIndex;
   string target=tableIndex==1?"MBR":tableIndex==2?"GPT":oldStyle;
   bool clusters=Find<CheckBox>("ConvertClusters").IsChecked==true;
   string recoveryDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NexusUsbFormatter","recovery");
   string journal;
   if(recovery) {
    using(var dialog=new System.Windows.Forms.OpenFileDialog {
     Title="Choose this drive's conversion journal", Filter="Nexus conversion recovery|*.nxcv",
     CheckFileExists=true, InitialDirectory=recoveryDirectory}) {
     if(dialog.ShowDialog(Host.FindForm())!=System.Windows.Forms.DialogResult.OK)return;
     journal=Path.GetFullPath(dialog.FileName);
    }
   } else journal=Path.Combine(recoveryDirectory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".nxcv");
   string identity=Convert.ToString(d["model"])+" / "+Size(d["size"])+"\nDisk "+d["number"]+" / serial "+d["serial"];
   string warning=recovery?
    "Restore the original ranges from this drive's interrupted conversion?\n\n"+identity+"\n\nUse only while the drive is still offline. Keep the journal until recovery is verified.":
    identity+"\n\nTable: "+oldStyle+" → "+target+"\nClusters: "+(clusters?"512 KB (increase only)":"unchanged")+
    "\n\nFiles are preserved, but interruption or hardware failure can still cause data loss. Continue only if you have a separate backup.\n\nRecovery staging uses free space on your PC; insufficient space stops before USB writes. No format fallback.";
   if(!NexusDialog.Confirm(owner(),recovery?"Restore interrupted conversion?":"Confirm file-preserving conversion",warning,recovery?"Recover":"Convert"))return;
   SetBusy(true,true);bool changed=false,complete=false;string result="",failure="";
   try {
    if(!recovery)Directory.CreateDirectory(recoveryDirectory);
    var request=new Dictionary<string,object>{{"schema",5},{"action",recovery?"recover-conversion":"convert-keep-files"},{"userInitiated",true},{"backupConfirmed",true},
     {"issuedUtc",DateTime.UtcNow.ToString("o")},{"appPath",appPath},{"logDirectory",logDirectory},{"disk",d},{"journalPath",journal},
     {"partitionNumber",partition==null?0:partition.Data["number"]},{"targetStyle",target},{"convertClusters",clusters}};
    int exit=await Backend(recovery?"Recover":"Convert",request,delegate(Dictionary<string,object> e) {
     string kind=Convert.ToString(e["kind"]);
     if(kind=="changed")changed=true;
     if(kind=="complete"){complete=true;result=Convert.ToString(e["message"]);}
     if(kind=="error")failure=Convert.ToString(e["message"]);
    });
    if(exit==0&&complete){stage.Text=result;Append("Recovery journal retained on PC: "+journal);}
    else {
     stage.Text=changed?"Interrupted / drive kept offline. Use Recover.":"Stopped before changes / see Logs.";
     Find<Expander>("ActivityExpander").IsExpanded=true;
     Dialog("Conversion stopped",(changed?"Keep the drive offline. Refresh, select that drive, then Recover using:\n":"No USB writes were reported. Staging files may remain at:\n")+journal+"\n\n"+failure);
    }
   }catch(Exception e){Append("Conversion failed: "+e);stage.Text="Operation stopped / check Logs and retain recovery files.";}
   finally{ready=false;drives.SelectedIndex=-1;SetBusy(false,false);}
  }
 }
}
