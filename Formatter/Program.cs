// In-process Windows Manager view for the Nexus USB Formatter.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms.Integration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace NexusFormatter {
 public sealed class DiskItem {
  public Dictionary<string,object> Data;
  public override string ToString() {
   return "Disk "+Data["number"]+"  |  "+Data["model"]+"  |  "+Program.Size(Data["size"]);
  }
 }
 public sealed partial class Program : IDisposable {
  readonly UserControl view;
  readonly Func<IntPtr> owner;
  public ElementHost Host {get;} = new ElementHost { Dock = System.Windows.Forms.DockStyle.Fill };
  public event Action BusyChanged;
  public bool Busy => busy;
  readonly ComboBox drives;
  readonly TextBox label,activity;
  readonly TextBlock details,safety,stage;
  readonly Button format,refresh,standard,large;
  readonly string logDirectory,logFile;
  readonly StreamWriter log;
  bool busy,formatting,ready;
  bool loggingHealthy=true;
  bool largeProfile;
  string Profile {get {return largeProfile?"large512":"standard512";}}
  string Style {get {return largeProfile?"GPT":"MBR";}}
  const string LoaderWarning="Requires a Switch loader with 64-bit USB support.";
  readonly string appPath=Environment.ProcessPath;
  public Program(Func<IntPtr> ownerHandle) {
   owner=ownerHandle;
   logDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NexusUsbFormatter","logs");
   Directory.CreateDirectory(logDirectory);
   logFile=Path.Combine(logDirectory,"formatter-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".log");
   log=new StreamWriter(logFile,false,new UTF8Encoding(false)) {AutoFlush=true};
   view=(UserControl)XamlReader.Parse(Resource("Formatter.xaml"));
   view.Resources=(ResourceDictionary)XamlReader.Parse(Resource("Theme.xaml"));
   Host.Child=view;
   drives=Find<ComboBox>("DriveList");label=Find<TextBox>("LabelBox");activity=Find<TextBox>("ActivityLog");
   details=Find<TextBlock>("DiskDetails");safety=Find<TextBlock>("DiskSafety");stage=Find<TextBlock>("StageText");
   format=Find<Button>("FormatButton");refresh=Find<Button>("RefreshButton");
   standard=Find<Button>("StandardButton");large=Find<Button>("LargeButton");
   standard.Click+=delegate {SelectProfile(false);};large.Click+=delegate {SelectProfile(true);};
   SelectProfile(false);
   var image=Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.png");
   if(image!=null) {var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.StreamSource=image;bi.EndInit();image.Dispose();Find<Image>("BrandLogo").Source=bi;}
   Find<TextBlock>("AccessState").Text="Windows asks for permission before disk operations";
   refresh.Click+=async delegate {await Refresh();};
   format.Click+=async delegate {await Format();};
   InitializeManager();
   // Detection may select a unique eligible disk in Keep files mode, never act.
   drives.SelectionChanged+=delegate {
    var selected=drives.SelectedItem as DiskItem;
    SelectProfile(selected!=null && NeedsGpt(selected.Data));
   };
   Find<Button>("LogsButton").Click+=delegate {Process.Start(new ProcessStartInfo("explorer.exe","\""+logDirectory+"\"") {UseShellExecute=true});};
   Append("Choose a USB drive. Keep files inspects or converts an existing drive.");
  }
  public Task RefreshAsync() => Refresh();
  public void Dispose() { Host.Dispose(); log.Dispose(); }
  internal static string Resource(string name) {
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
    if(stream==null)throw new InvalidOperationException("Missing embedded resource: "+name);
    using(var reader=new StreamReader(stream))return reader.ReadToEnd();
   }
  }
  T Find<T>(string name) where T:class {return view.FindName(name) as T;}
  internal static string Size(object size) {
   double bytes=Convert.ToDouble(size,CultureInfo.InvariantCulture);
   return (bytes/(bytes>=1000000000000.0?1000000000000.0:1000000000.0)).ToString("0.0",CultureInfo.InvariantCulture)+(bytes>=1000000000000.0?" TB":" GB");
  }
  static string[] Strings(object data) {
   var result=new List<string>();
   if(data is string)result.Add((string)data);
   else if(data is IEnumerable)foreach(var item in (IEnumerable)data)result.Add(Convert.ToString(item,CultureInfo.InvariantCulture));
   return result.ToArray();
  }
  static List<Dictionary<string,object>> Objects(object data) {
   var list=new List<Dictionary<string,object>>();
   if(data is Dictionary<string,object>)list.Add((Dictionary<string,object>)data);
   else if(data is IEnumerable)foreach(var o in (IEnumerable)data)if(o is Dictionary<string,object>)list.Add((Dictionary<string,object>)o);
   return list;
  }
  void Append(string message) {
   var line="["+DateTime.Now.ToString("HH:mm:ss")+"] "+message;
   // If the log cannot be written, do not permit another format.
   try {log.WriteLine(line);} catch {loggingHealthy=false;ready=false;format.IsEnabled=false;stage.Text="Logging failed. No new format can start.";}
   activity.AppendText(line+Environment.NewLine);
   if(activity.Text.Length>60000)activity.Text=activity.Text.Substring(activity.Text.Length-40000);
   activity.ScrollToEnd();
  }
  void SetBusy(bool value,bool writes) {
   busy=value;formatting=value && writes;drives.IsEnabled=!value;label.IsEnabled=!value;refresh.IsEnabled=!value;
   var selected=drives.SelectedItem as DiskItem;
   standard.IsEnabled=!value && (selected==null || !NeedsGpt(selected.Data));large.IsEnabled=!value;
   format.IsEnabled=!value && ready && loggingHealthy && !keepFiles;
   UpdateManagerControls();
   BusyChanged?.Invoke();
  }
  static bool LoaderLimited(Dictionary<string,object> d) {
   return Convert.ToDecimal(d["size"])>(decimal)uint.MaxValue*Convert.ToInt32(d["sector"]);
  }
  static bool NeedsGpt(Dictionary<string,object> d) {
   return Convert.ToDecimal(d["size"])>2000000000000m;
  }
  void SelectProfile(bool useLarge) {
   if(busy)return;
   var selected=drives.SelectedItem as DiskItem;
   if(selected!=null && NeedsGpt(selected.Data))useLarge=true;
   largeProfile=useLarge;
   standard.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(useLarge?"#24314C":"#524A91"));
   large.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(useLarge?"#524A91":"#24314C"));
   Find<TextBlock>("ProfileStyle").Text=Style;
   Find<TextBlock>("ProfileHeading").Text=useLarge?"GPT / large drives":"MBR / standard";
   Find<TextBlock>("ProfileHint").Text=useLarge?"Uses full capacity. Required above 2 TB.":"For drives up to 2 TB.";
   ShowSelected();
  }
  void ShowSelected() {
   ResetManager();
   ready=false;format.IsEnabled=false;
   var item=drives.SelectedItem as DiskItem;
   if(item==null) {standard.IsEnabled=!busy;details.Text="Select a USB drive to see its details.";safety.Text=drives.Items.Count>1?"Choose the USB drive you want to use.":"Connect a USB drive, then Refresh.";return;}
   var d=item.Data;
   var sb=new StringBuilder();
   sb.Append(d["model"]).Append("  /  ").Append(Size(d["size"])).Append("\n");
   sb.Append("Physical disk ").Append(d["number"]).Append("  /  Current style: ").Append(d["style"]).Append("\n");
   sb.Append("Logical sector: ").Append(d["sector"]).Append(" bytes\n");
   string serial=Convert.ToString(d["serial"]);sb.Append("Serial / ID: ").Append(string.IsNullOrWhiteSpace(serial)?Convert.ToString(d["uniqueId"]):serial);
   var partitions=Objects(d["partitions"]);
   if(partitions.Count==0)sb.Append("\nNo partitions / no drive letter needed.");
   foreach(var part in partitions) {
    sb.Append("\n").Append(string.IsNullOrEmpty(Convert.ToString(part["letter"]))?"No letter":part["letter"]+":")
      .Append("  ").Append(part["label"]).Append("  /  ").Append(part["filesystem"]).Append("  /  ").Append(Size(part["size"]));
   }
   details.Text=sb.ToString();
   ready=Convert.ToBoolean(d["allowed"]);
   safety.Text=ready?(keepFiles?"Ready / no changes yet.":"WARNING: all files on this drive will be erased."):string.Join("\n",Strings(d["reasons"]));
   standard.IsEnabled=!busy && !NeedsGpt(d);
   if(!largeProfile && NeedsGpt(d)) {
    ready=false;safety.Text+="\nGPT is required above 2 TB. Refresh and select the drive again.";
   }
   if(Convert.ToDecimal(d["size"])>140737488355328m) {ready=false;safety.Text+="\nBeyond this formatter's 128 TiB limit.";}
   if(LoaderLimited(d))safety.Text+="\n\n"+LoaderWarning;
   safety.Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(ready && !LoaderLimited(d)?"#B2C2DB":"#FFB5AB"));
   format.IsEnabled=ready && !busy && loggingHealthy && !keepFiles;
   ShowPartitions(d);
  }
  async Task Refresh() {
   if(busy)return;
   ready=false;drives.Items.Clear();ShowSelected();SetBusy(true,false);stage.Text="Finding USB drives...";
   var detected=new List<Dictionary<string,object>>();bool valid=false;
   try {
    int inventory=0;
    var request=new Dictionary<string,object>{{"appPath",appPath},{"logDirectory",logDirectory}};
    int exit=await Backend("Inventory",request,delegate(Dictionary<string,object> e) {
     if(Convert.ToString(e["kind"])=="inventory") {
      inventory++;detected.AddRange(Objects(e["data"]));
     }
    });
    valid=exit==0 && inventory==1;
    stage.Text=valid?(detected.Count==0?"No USB drives found.":"Choose a drive."):"Drive detection failed. See Logs.";
   } catch(Exception e) {Append("Inspection failed: "+e.Message);stage.Text="Inspection failed. No disk was changed.";}
   finally {SetBusy(false,false);}
   if(valid){foreach(var d in detected)drives.Items.Add(new DiskItem{Data=d});SelectDetectedDrive();}
  }
  async Task Format() {
   if(busy || keepFiles || !ready || !loggingHealthy)return;
   var chosen=drives.SelectedItem as DiskItem;if(chosen==null)return;
   string name=label.Text;
   if(!Regex.IsMatch(name,@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,10}\z") || name!=name.Trim()) {
    Dialog("Check the drive name","Use 1-11 letters, numbers, spaces, underscores or hyphens. Do not start or end with a space.");
    return;
   }
   if(!NexusDialog.Confirm(owner(), "Erase and format this USB drive?",
       chosen+"\n\nAll files and partitions on this drive will be permanently erased.\nNew layout: "+Style+" / exFAT / 512 KiB.", "Erase drive"))return;
   SetBusy(true,true);stage.Text="Rechecking the selected physical drive before erasing...";
   bool complete=false,writes=false;string failure="",hint="",successMessage="";
   try {
    var request=MakeFormatRequest(chosen,name);
    int exit=await Backend("Format",request,delegate(Dictionary<string,object> e) {
     string kind=Convert.ToString(e["kind"]);
     if(kind=="destructive")writes=true;
     if(kind=="complete") {complete=true;successMessage=Convert.ToString(e["message"]);}
     if(kind=="error") {
      failure=Convert.ToString(e["message"]);
      var data=e.ContainsKey("data")?e["data"] as Dictionary<string,object>:null;
      if(data!=null && data.ContainsKey("hint"))hint=Convert.ToString(data["hint"]);
     }
    });
    if(exit==0 && complete) {
     stage.Text=successMessage;
     Append("Native format metadata and Windows mount verified. Safely eject the USB drive before disconnecting.");
    } else {
     stage.Text="Stopped. Check the activity log before using the drive.";
     string summary=writes?"The old partitions may already be gone. No erase/format retry was attempted. Keep the log; do not assume the drive is ready.":"Stopped before erasing. No format was started.";
     Dialog("Formatting stopped",summary+"\n\n"+(string.IsNullOrEmpty(hint)?failure:hint)+"\n\nFull technical details: Open logs.");
    }
   } catch(Exception e) {
    Append("Operation error: "+e.Message);
    stage.Text="Operation failed. Review the saved log.";
    Dialog("Operation stopped","Could not complete or verify the operation. If erasing had started, data may already be gone. No automatic retry is attempted.\n\n"+e.Message);
   } finally {ready=false;drives.SelectedIndex=-1;SetBusy(false,false);}
  }
  Dictionary<string,object> MakeFormatRequest(DiskItem chosen,string name) {
   return new Dictionary<string,object>{
    {"schema",3},{"action","format-selected-usb"},{"userInitiated",true},{"profile",Profile},
    {"disk",chosen.Data},{"label",name},{"issuedUtc",DateTime.UtcNow.ToString("o")},
    {"appPath",appPath},{"logDirectory",logDirectory}};
  }
  async Task<int> Backend(string mode,Dictionary<string,object> request,Action<Dictionary<string,object>> onEvent) {
   return await DiskBackend.RunAsync(mode,request,line => {
    var item=DiskBackend.Event(line);
    if(item!=null && item.ContainsKey("kind") && item.ContainsKey("message")) {
     var message=Convert.ToString(item["message"]);Append(message);stage.Text=message;onEvent(item);
    } else Append(line);
   });
  }
  void Dialog(string title,string message) {NexusDialog.Notice(owner(),title,message,DialogTone.Error);}
 }
}
