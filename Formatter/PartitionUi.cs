using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NexusFormatter {
 public sealed class PartitionItem {
  public Dictionary<string,object> Data;
  public override string ToString() {return "#"+Data["number"]+"  "+Data["letter"]+"  "+Data["filesystem"]+"  "+Program.Size(Data["size"]);}
 }
 public sealed partial class Program {
  bool keepFiles=true,managerInitialized;
  ComboBox partitions;
  Button inspectPartition;
  void InitializeManager() {
   partitions=Find<ComboBox>("PartitionList");inspectPartition=Find<Button>("InspectPartition");managerInitialized=true;
   Find<RadioButton>("KeepFilesMode").Checked+=delegate {ChangeMode(true);};
   Find<RadioButton>("EraseMode").Checked+=delegate {ChangeMode(false);};
   partitions.SelectionChanged+=delegate {ShowPartitionHint();UpdateManagerControls();};
   inspectPartition.Click+=async delegate {await InspectPartition();};
   Find<Button>("ConvertButton").Click+=async delegate {await ConvertPartition(false);};
   Find<Button>("RecoverButton").Click+=async delegate {await ConvertPartition(true);};
   ChangeMode(true);
  }
  void ChangeMode(bool preserve) {
   if(busy)return;
   keepFiles=preserve;
   Find<StackPanel>("ManagerPanel").Visibility=preserve?Visibility.Visible:Visibility.Collapsed;
   Find<StackPanel>("FormatPanel").Visibility=preserve?Visibility.Collapsed:Visibility.Visible;
   format.Visibility=preserve?Visibility.Collapsed:Visibility.Visible;
   inspectPartition.Visibility=preserve?Visibility.Visible:Visibility.Collapsed;
   Find<Button>("ConvertButton").Visibility=preserve?Visibility.Visible:Visibility.Collapsed;
   Find<TextBlock>("ModeWarning").Text=preserve?"No changes until confirmed.":"Erases the entire drive after confirmation.";
   ShowSelected();UpdateManagerControls();
  }
  void ResetManager() {
   if(!managerInitialized)return;
   partitions.Items.Clear();
   Find<Grid>("PartitionMap").Children.Clear();Find<Grid>("PartitionMap").ColumnDefinitions.Clear();
   Find<TextBlock>("MapLegend").Text="Select a drive to see its layout.";
   ShowPartitionHint();UpdateManagerControls();
  }
  void ShowPartitions(Dictionary<string,object> disk) {
   if(!managerInitialized)return;
   var parts=Objects(disk["partitions"]);parts.Sort(delegate(Dictionary<string,object> a,Dictionary<string,object> b){return Convert.ToDecimal(a["offset"]).CompareTo(Convert.ToDecimal(b["offset"]));});
   decimal total=Convert.ToDecimal(disk["size"]),end=0;var legend=new StringBuilder();
   bool geometryValid=total>0;
   foreach(var p in parts) {
    decimal start=Convert.ToDecimal(p["offset"]),length=Convert.ToDecimal(p["size"]);
    if(start<end || length<=0 || start>total || length>total-start)geometryValid=false;
    else end=start+length;
    partitions.Items.Add(new PartitionItem {Data=p});
   }
   if(!geometryValid) {Find<TextBlock>("MapLegend").Text="Invalid/overlapping layout. Select a partition for read-only findings; do not modify.";UpdateManagerControls();return;}
   end=0;
   foreach(var p in parts) {
    decimal start=Convert.ToDecimal(p["offset"]),length=Convert.ToDecimal(p["size"]);
    if(start>end)MapSegment("Gap",start-end,total,"#24314B");
    string name="#"+p["number"]+" "+p["filesystem"];
    MapSegment(name,length,total,"#6E64AB");end=start+length;
    if(legend.Length>0)legend.Append("  |  ");
    legend.Append(name).Append(" ").Append(Size(length));
   }
   if(end<total)MapSegment("Unallocated",total-end,total,"#24314B");
   Find<TextBlock>("MapLegend").Text=parts.Count==0?"No partitions / format required to create one.":legend.ToString();
   int single=-1;
   for(int i=0;i<partitions.Items.Count;i++)if(Convert.ToString(((PartitionItem)partitions.Items[i]).Data["filesystem"]).Equals("exFAT",StringComparison.OrdinalIgnoreCase)){if(single!=-1){single=-2;break;}single=i;}
   if(single>=0)partitions.SelectedIndex=single;
   UpdateManagerControls();
  }
  void MapSegment(string name,decimal length,decimal total,string color) {
   var map=Find<Grid>("PartitionMap");int column=map.ColumnDefinitions.Count;
   map.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength((double)(length/total),GridUnitType.Star)});
   var box=new Border {Background=(Brush)new BrushConverter().ConvertFromString(color),BorderBrush=Brushes.SlateGray,BorderThickness=new Thickness(0.5),ClipToBounds=true,
    ToolTip=name+" / "+length.ToString("N0",CultureInfo.InvariantCulture)+" bytes",Child=new TextBlock {Text=name,Margin=new Thickness(7),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis}};
   Grid.SetColumn(box,column);map.Children.Add(box);
  }
  void ShowPartitionHint() {
   if(!managerInitialized)return;
   var p=partitions.SelectedItem as PartitionItem;
   string fs=p==null?"":Convert.ToString(p.Data["filesystem"]);
   Find<TextBlock>("PartitionHint").Text=p==null?"Select an exFAT partition.":fs.Equals("exFAT",StringComparison.OrdinalIgnoreCase)?
    "Preserves files, labels and partition size.":"Not reported as exFAT. Conversion unavailable.";
   Find<TextBox>("InspectionReport").Text="No report yet. Check drive to inspect current settings.";
  }
  void UpdateManagerControls() {
   if(!managerInitialized)return;
   Find<RadioButton>("KeepFilesMode").IsEnabled=!busy;Find<RadioButton>("EraseMode").IsEnabled=!busy;
   partitions.IsEnabled=!busy && keepFiles;
   var disk=drives.SelectedItem as DiskItem;var p=partitions.SelectedItem as PartitionItem;
   // Read-only inspection is also useful for a blocked, read-only or unhealthy disk.
   inspectPartition.IsEnabled=!busy && keepFiles && disk!=null && p!=null && loggingHealthy;
   Find<ComboBox>("ConversionTarget").IsEnabled=!busy;Find<CheckBox>("ConvertClusters").IsEnabled=!busy;
   Find<Button>("ConvertButton").IsEnabled=!busy&&keepFiles&&ready&&disk!=null&&p!=null&&Convert.ToString(p.Data["filesystem"]).Equals("exFAT",StringComparison.OrdinalIgnoreCase)&&loggingHealthy;
   Find<Button>("RecoverButton").IsEnabled=!busy&&disk!=null&&disk.Data.ContainsKey("offline")&&Convert.ToBoolean(disk.Data["offline"])&&loggingHealthy;
  }
  async Task InspectPartition() {
   if(busy || !keepFiles || !loggingHealthy)return;
   var disk=drives.SelectedItem as DiskItem;var p=partitions.SelectedItem as PartitionItem;
   if(disk==null || p==null)return;
   Find<TextBox>("InspectionReport").Text="Reading existing settings. No changes are being made.";
   Find<Expander>("ReportExpander").IsExpanded=true;
   SetBusy(true,false);
   try {
    var request=new Dictionary<string,object>{{"schema",4},{"action","inspect-partition"},{"userInitiated",true},{"issuedUtc",DateTime.UtcNow.ToString("o")},
     {"appPath",appPath},{"logDirectory",logDirectory},{"disk",disk.Data},{"partitionNumber",p.Data["number"]}};
    Dictionary<string,object> report=null;string error="No inspection report was returned.";
    int exit=await Backend("InspectPartition",request,delegate(Dictionary<string,object> e) {
     if(Convert.ToString(e["kind"])=="report")report=e["data"] as Dictionary<string,object>;
     if(Convert.ToString(e["kind"])=="error")error=Convert.ToString(e["message"]);
    });
    if(exit!=0 || report==null) {Find<TextBox>("InspectionReport").Text="Inspection incomplete. No changes were made.\n\n"+error;return;}
    Find<TextBox>("InspectionReport").Text=Convert.ToString(report["summary"]);
    stage.Text=Convert.ToString(report["status"])+" / report saved.";
   } catch(Exception e) {Find<TextBox>("InspectionReport").Text="Inspection failed. No disk changes.\n\n"+e.Message;Append("Inspection failed: "+e.Message);}
   finally {SetBusy(false,false);}
  }
 }
}
