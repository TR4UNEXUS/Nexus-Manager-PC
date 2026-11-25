using System.Runtime.InteropServices;
using System.Text;
using Hats.Core;
using Nexus.Pc;

namespace Nexus.Desktop;

record Volume(string Root,string Label,string Format,long Free,uint Serial)
{
    public override string ToString()=>$"{Root}   {Label}   ·   {Free/1073741824d:0.#} GiB free";
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern bool GetVolumeInformation(string root,StringBuilder? label,int labelSize,out uint serial,out uint max,out uint flags,StringBuilder? fs,int fsSize);
    public static Volume? Find(DriveInfo d)
    {
        try
        {
            if(d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady ||
               string.Equals(d.Name,Path.GetPathRoot(Environment.SystemDirectory),StringComparison.OrdinalIgnoreCase)) return null;
            var dev=WindowsStorageDevices.Read(d.Name);
            if(dev?.BusType!=7 || dev.IsHekateUsb || !(d.DriveFormat is "FAT32" or "exFAT"))return null;
            if(File.Exists(Path.Combine(d.Name,"atmosphere","package3")) || Directory.Exists(Path.Combine(d.Name,"emuMMC")))return null;
            return GetVolumeInformation(d.Name,null,0,out var serial,out _,out _,null,0)
                ?new(d.Name,d.VolumeLabel,d.DriveFormat,d.AvailableFreeSpace,serial):null;
        }
        catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
    }
    public void Check()
    {
        if(!GetVolumeInformation(Root,null,0,out var s,out _,out _,null,0)||s!=Serial)
            throw new InstallDestinationException("The selected USB volume disconnected or changed. Reconnect it and refresh the drive list.");
    }
}
static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>0)
        {
            if(args.Length!=3||!int.TryParse(args[2],out var parent))return 1;
            return args[0] switch
            {
                "--format-helper" => FormatElevation.RunHelperAsync(args[1],parent).GetAwaiter().GetResult(),
                "--usb-operation" => NexusFormatter.DiskBackend.WorkerAsync(args[1],parent).GetAwaiter().GetResult(),
                _ => 1
            };
        }
        try { CompressionJob.RecoverAbandoned(); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        using var running = new Mutex(false,@"Local\NexusManagerPCDesktop-Running");
        ApplicationConfiguration.Initialize();Application.Run(new LibraryWindow());return 0;
    }
}
partial class LibraryWindow:Form
{
    internal const string Requirements="PC sync requires Nexus HATS NX1 (any edition) and Switch Manager 0.2.33 or later.";
    readonly ComboBox drives=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=326};
    readonly Label keysLabel=new(){AutoSize=true};
    readonly Label status=new(){Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
    readonly ListView list=new NexusTable{View=View.Details,Dock=DockStyle.Fill,FullRowSelect=true,ShowItemToolTips=true,MultiSelect=true,BorderStyle=BorderStyle.None,HideSelection=false};
    readonly OperationProgressBar bar=new();
    readonly UsbLibraryPane library=new();
    readonly TableLayoutPanel install=new(){Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};
    readonly System.Windows.Forms.Timer detector=new(){Interval=4000};
    readonly List<Button> inputs=[];
    readonly Button go,stop,removeQueued,scanSavings;
    readonly CheckBox compressInstall=new(){Text="Compress before installing",AutoSize=true,Margin=new Padding(12,8,3,3)};
    readonly WorkspaceTab libraryTab,installTab,hatsTab,formatTab;
    readonly WorkspaceEmpty queueEmpty=new(WorkspaceIcon.Install);
    readonly HatsPane hats=new();
    readonly Panel formatter=new(){Dock=DockStyle.Fill};
    NexusFormatter.Program? formatterView;
    readonly FlowLayoutPanel settings=Row();
    readonly Label requirements=new(){Text=Requirements,AutoSize=true,Padding=new Padding(3,5,3,3)};
    CancellationTokenSource? discovery;
    readonly Queue<string[]> drops=new();
    bool ToolBusy => hats.Busy || formatterView?.Busy == true;
    bool CanQueue => !closing && active==null && !library.IsWriting && !ToolBusy;
    enum Page { Library,Install,Hats,Formatter }
    Page page;
    readonly MenuStrip menu=new();
    readonly List<string> queue=[];
    CancellationTokenSource? active;
    string? keys;
    bool binding,closing,waitingForScan;

    public LibraryWindow()
    {
        Text="Nexus Manager for PC";Font=new Font("Segoe UI",9.5f);
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition=FormStartPosition.Manual;AllowDrop=true;WindowState=FormWindowState.Normal;
        var area=Screen.FromPoint(Cursor.Position).WorkingArea;
        int width=Math.Min(LogicalToDeviceUnits(1180),(int)(area.Width*.90));
        int height=Math.Min(LogicalToDeviceUnits(770),(int)(area.Height*.90));
        MinimumSize=new Size(Math.Min(width,800),Math.Min(height,500));
        Bounds=new Rectangle(area.X+(area.Width-width)/2,area.Y+(area.Height-height)/2,width,height);

        if(NexusTheme.Enabled){BackColor=NexusTheme.Background;ForeColor=NexusTheme.Text;keysLabel.ForeColor=NexusTheme.Secondary;}
        HandleCreated+=(_,_)=>NexusTheme.TitleBar(this);
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12,4,12,5),ColumnCount=1,RowCount=5};
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute,52));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(grid);MainMenuStrip=menu;
        var fileMenu=new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Add game &packages…",null,(_,_)=>{ShowPage(Page.Install);PickFiles();}){ShortcutKeys=Keys.Control|Keys.O});
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Choose prod.&keys…",null,(_,_)=>ChooseKeys()));
        updateCommand.Click+=async (_,_)=>await CheckDesktopUpdate(true);
        fileMenu.DropDownItems.Add(updateCommand);
        fileMenu.DropDownItems.Add("Support &Nexus…",null,(_,_)=>ShowProjectSupport());
        fileMenu.DropDownItems.Add(new ToolStripSeparator());fileMenu.DropDownItems.Add("E&xit",null,(_,_)=>Close());
        var viewMenu=new ToolStripMenuItem("&View");viewMenu.DropDownItems.Add(new ToolStripMenuItem("Refresh library",null,(_,_)=>{RefreshDrives();BindLibrary(true);}){ShortcutKeys=Keys.F5});
        menu.Items.Add(fileMenu);menu.Items.Add(viewMenu);menu.Items.Add(CreateCoffeeButton());
        menu.ShowItemToolTips=true;
        void SizeCoffeeButton() => menu.ImageScalingSize=LogicalToDeviceUnits(new Size(114,32));
        SizeCoffeeButton(); DpiChanged+=(_,_)=>SizeCoffeeButton();
        NexusTheme.MenuStyle(menu);
        grid.Controls.Add(new WorkspaceHeader(menu),0,0);

        settings.AutoSize=true;settings.WrapContents=true;settings.Padding=new Padding(5,5,0,5);
        settings.Controls.Add(new Label{Text="USB drive",AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,4,9,4)});
        drives.Width=LogicalToDeviceUnits(305);drives.Anchor=AnchorStyles.Left;settings.Controls.Add(drives);
        settings.Controls.Add(Btn("Choose prod.keys…",ChooseKeys));
        keysLabel.Anchor=AnchorStyles.Left;keysLabel.Margin=new Padding(6,4,0,4);settings.Controls.Add(keysLabel);grid.Controls.Add(settings,0,2);

        if(NexusTheme.Enabled){
            NexusTheme.NativeDark(drives);
            drives.FlatStyle=FlatStyle.Flat;drives.BackColor=NexusTheme.Header;drives.ForeColor=NexusTheme.Text;
            drives.DrawMode=DrawMode.OwnerDrawFixed;drives.ItemHeight=Math.Max(drives.ItemHeight,Font.Height+4);
            drives.DrawItem+=(_,e)=>{
                bool highlighted=e.State.HasFlag(DrawItemState.Selected);
                using var fill=new SolidBrush(highlighted?NexusTheme.Selection:NexusTheme.Header);
                e.Graphics.FillRectangle(fill,e.Bounds);
                var rect=e.Bounds;rect.Inflate(-4,0);
                string value=e.Index>=0?drives.Items[e.Index]?.ToString()??"":"Select a USB drive";
                TextRenderer.DrawText(e.Graphics,value,Font,rect,NexusTheme.Text,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
        }
        var tabs=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=Padding.Empty};
        WorkspaceTab Tab(string name, WorkspaceIcon glyph, Page destination) {
            var tab=new WorkspaceTab(name,glyph,()=>ShowPage(destination));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
            tabs.Controls.Add(tab,tabs.Controls.Count,0);inputs.Add(tab);return tab;
        }
        libraryTab=Tab("&USB library",WorkspaceIcon.Library,Page.Library);
        installTab=Tab("&Install games",WorkspaceIcon.Install,Page.Install);
        hatsTab=Tab("&HATS Installer",WorkspaceIcon.Hats,Page.Hats);
        formatTab=Tab("USB &Formatter",WorkspaceIcon.Formatter,Page.Formatter);
        grid.Controls.Add(tabs,0,1);
        var pages=new Panel{Dock=DockStyle.Fill};pages.Controls.AddRange([install,library,hats,formatter]);grid.Controls.Add(pages,0,3);
        install.Padding=new Padding(8);install.RowStyles.Add(new RowStyle(SizeType.AutoSize));install.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        install.RowStyles.Add(new RowStyle(SizeType.Absolute,Math.Max(32,Font.Height+12)));install.RowStyles.Add(new RowStyle(SizeType.Absolute,26));install.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var queueRow=Row();queueRow.AutoSize=true;queueRow.Controls.Add(Btn("Add game packages…",PickFiles));queueRow.Controls.Add(Btn("Add folder…",PickFolder));removeQueued=Btn("Remove selected",Remove);queueRow.Controls.Add(removeQueued);
        scanSavings=Btn("Scan savings",async()=>await Run(scanOnly:true));queueRow.Controls.Add(scanSavings);install.Controls.Add(queueRow,0,0);
        list.SelectedIndexChanged+=(_,_)=>Ready();
        list.Columns.Add("Install queue",LogicalToDeviceUnits(460));list.Columns.Add("Package size",LogicalToDeviceUnits(120));
        var queuePanel=new Panel{Dock=DockStyle.Fill};queuePanel.Controls.Add(list);queuePanel.Controls.Add(queueEmpty);
        queueEmpty.SetMessage("Drop packages or folders here","Add NSP, NSX, NSZ, XCI or XCZ files to your install queue.");
        install.Controls.Add(queuePanel,0,1);
        list.Resize+=(_,_)=>list.Columns[0].Width=Math.Max(180,list.ClientSize.Width-list.Columns[1].Width);
        status.Text="";install.Controls.Add(status,0,2);install.Controls.Add(bar,0,3);
        var actions=Row();actions.AutoSize=true;actions.Padding=new Padding(0,5,0,0);go=Btn("Review and install",async()=>await Run());
        stop=Btn("Cancel",()=>{active?.Cancel();discovery?.Cancel();});stop.Enabled=false;actions.Controls.Add(go);actions.Controls.Add(stop);install.Controls.Add(actions,0,4);
        actions.Controls.Add(compressInstall);
        requirements.ForeColor=NexusTheme.Enabled?NexusTheme.Secondary:SystemColors.ControlText;grid.Controls.Add(requirements,0,4);
        keys=RememberedKeys.Current.Restore();
        keysLabel.Text=keys==null?"Choose once to load your library":"prod.keys remembered on this PC";
        drives.SelectedIndexChanged+=(_,_)=>{if(!binding)BindLibrary();};
        detector.Tick+=(_,_)=>{RefreshDrives();OfferUpdate();};
        library.WriteStateChanged+=()=>{LockControls();if(closing&&!library.IsWriting)BeginInvoke(Close);};
        Shown+=async (_,_)=>{ShowPage(Page.Library);RefreshDrives();detector.Start();await CheckDesktopUpdate(false);};
        hats.BusyChanged+=LockControls;
        DragEnter+=(_,e)=>{if(CanQueue&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true)e.Effect=e.AllowedEffect&DragDropEffects.Copy;};
        DragDrop+=async (_,e)=>{if(CanQueue&&e.Data?.GetData(DataFormats.FileDrop) is string[] names){ShowPage(Page.Install);await AddAsync(names);}};
        FormClosing+=async (_,e)=>{
            if(ToolBusy){e.Cancel=true;MessageBox.Show(this,"Wait for the current drive operation to finish before closing Manager.","Drive in use");return;}
            if(active!=null||library.IsWriting||discovery!=null){closing=true;discovery?.Cancel();active?.Cancel();library.Cancel();e.Cancel=true;status.Text="Stopping safely…";}
            else{
                detector.Stop();
                var readers=library.StopReading();
                if(!readers.IsCompleted){
                    e.Cancel=true;
                    if(waitingForScan)return;
                    closing=waitingForScan=true;
                    await readers;
                    if(!IsDisposed)BeginInvoke(Close);
                }else formatterView?.Dispose();
            }
        };
        FormClosed+=(_,_)=>{updateLifetime.Cancel();updateLifetime.Dispose();detector.Dispose();};
    }

    protected override void WndProc(ref Message message){
        base.WndProc(ref message);
        const int deviceChange=0x0219;
        if(message.Msg==deviceChange && IsHandleCreated && !Disposing && !IsDisposed && Visible)
            BeginInvoke(()=>{if(!IsDisposed)RefreshDrives();});
    }
    static FlowLayoutPanel Row()=>new(){Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
    internal static Button ButtonFor(string title,Action action){
        var b=new WorkspaceCommand{Text=title,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlatStyle=FlatStyle.System,
            Padding=new Padding(6,2,6,2),Margin=new Padding(3,2,3,2),UseVisualStyleBackColor=true};
        NexusTheme.ButtonStyle(b);b.Click+=(_,_)=>action();return b;
    }
    Button Btn(string title,Action click){var b=ButtonFor(title,click);inputs.Add(b);return b;}
    void ShowPage(Page choice){
        if(choice!=page && (active!=null||library.IsWriting||ToolBusy||discovery!=null))return;
        page=choice;
        settings.Visible=requirements.Visible=page is Page.Library or Page.Install;
        library.Visible=page==Page.Library;install.Visible=page==Page.Install;
        hats.Visible=page==Page.Hats;formatter.Visible=page==Page.Formatter;
        libraryTab.Selected=page==Page.Library;installTab.Selected=page==Page.Install;
        hatsTab.Selected=page==Page.Hats;formatTab.Selected=page==Page.Formatter;
        if(page is Page.Hats or Page.Formatter){
            library.SetSource(null,null);
            if(page==Page.Hats)hats.OpenTab();
            else if(formatterView==null)
            {
                try
                {
                    formatterView=new NexusFormatter.Program(()=>Handle);
                    formatterView.BusyChanged+=LockControls;
                    formatter.Controls.Add(formatterView.Host);
                    _=formatterView.RefreshAsync();
                }
                catch(Exception error)
                {
                    formatterView?.Dispose();formatterView=null;
                    MessageBox.Show(this,error.GetBaseException().Message,"Could not open USB Formatter");
                }
            }
        }else BindLibrary();
    }
    void BindLibrary(bool force=false){
        if(page is Page.Library or Page.Install)library.SetSource(page==Page.Library?drives.SelectedItem as Volume:null,keys,force);
        Ready();
    }
    void RefreshDrives(){
        if(active!=null||library.IsWriting||closing||page is Page.Hats or Page.Formatter)return;
        var before=drives.Items.Cast<Volume>().ToArray();
        var found=DriveInfo.GetDrives().Select(Volume.Find).OfType<Volume>().ToArray();
        if(before.Select(v=>(v.Root,v.Serial,v.Label)).SequenceEqual(found.Select(v=>(v.Root,v.Serial,v.Label))))return;
        var prior=drives.SelectedItem as Volume;
        binding=true;
        try{
            drives.Items.Clear();drives.Items.AddRange(found);
            var selected=found.FirstOrDefault(v=>v.Root==prior?.Root && v.Serial==prior.Serial);
            if(selected==null){
                if(found.Length==1)selected=found[0];
                else{var known=found.Where(v=>Directory.Exists(Path.Combine(v.Root,"registered"))).Take(2).ToArray();if(known.Length==1)selected=known[0];}
            }
            drives.SelectedItem=selected;
        }finally{binding=false;}
        BindLibrary();
    }
    void LockControls(){
        bool idle=CanQueue&&discovery==null;
        foreach(var b in inputs)b.Enabled=idle;
        compressInstall.Enabled=idle;drives.Enabled=idle;menu.Enabled=idle;stop.Enabled=active!=null||discovery!=null;Ready();
    }
    const string SupportMessage = "If Nexus has been useful to you, consider buying me a coffee. Your support gives me more time to build new features and test fixes. Thanks for supporting my work!";

    ToolStripMenuItem CreateCoffeeButton()
    {
        using var stream = typeof(LibraryWindow).Assembly.GetManifestResourceStream("Nexus.Desktop.CoffeeButton.png")!;
        using var original = Image.FromStream(stream);
        var image = new Bitmap(original);
        var button = new ToolStripMenuItem("&Buy me a coffee", image, (_,_) => OpenCoffeePage()) {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            AccessibleName = "Buy me a coffee",
            AccessibleDescription = SupportMessage,
            ToolTipText = SupportMessage
        };
        button.Disposed += (_,_) => image.Dispose();
        return button;
    }

    void ShowProjectSupport()
    {
        var coffee = new TaskDialogButton("Buy me a coffee");
        var page = new TaskDialogPage {
            Caption = "Nexus Manager for PC",
            Heading = "Support Nexus",
            Text = SupportMessage,
            Buttons = { coffee, TaskDialogButton.Close },
            DefaultButton = TaskDialogButton.Close,
            AllowCancel = true
        };
        if (TaskDialog.ShowDialog(this, page) == coffee) OpenCoffeePage();
    }

    void OpenCoffeePage()
    {
        const string url = "https://www.buymeacoffee.com/TechRepairs4u";
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) {
            MessageBox.Show(this, "Windows could not open your browser. You can visit " + url, "Support Nexus");
        }
    }
    void ChooseKeys(){
        using var dialog=new OpenFileDialog{Filter="Switch keys (*.keys)|*.keys",Title="Select your local prod.keys"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{RememberedKeys.Current.Save(dialog.FileName);keys=RememberedKeys.Current.Store;keysLabel.Text="prod.keys remembered on this PC";}
        catch(Exception e){MessageBox.Show(this,"Could not remember this key file. "+e.Message,"Choose prod.keys");return;}
        BindLibrary(true);
    }
    async void PickFiles()
    {
        using var picker=new OpenFileDialog{Filter="Switch game packages|*.nsp;*.nsx;*.nsz;*.xci;*.xcz",Multiselect=true};
        if(picker.ShowDialog(this)==DialogResult.OK)await AddAsync(picker.FileNames);
    }
    async void PickFolder()
    {
        using var picker=new FolderBrowserDialog{Description="Add game packages from this folder and its subfolders"};
        if(picker.ShowDialog(this)==DialogResult.OK)await AddAsync([picker.SelectedPath]);
    }
    async Task AddAsync(string[] paths)
    {
        if(!CanQueue)return;
        drops.Enqueue(paths);
        if(discovery!=null)return;
        discovery=new();
        LockControls();
        int added=0,duplicates=0,unreadable=0;
        try
        {
            while(drops.TryDequeue(out var batch))
            {
                status.Text="Looking for game packages…";
                var alreadyQueued=queue.ToArray();var token=discovery.Token;
                var scan=await Task.Run(()=>PackageDiscovery.Scan(batch,alreadyQueued,token),token);
                token.ThrowIfCancellationRequested();
                list.BeginUpdate();
                try
                {
                    foreach(var file in scan.Files)
                    {
                        queue.Add(file.Path);
                        list.Items.Add(new ListViewItem([Path.GetFileName(file.Path),$"{file.Size/1048576d:N1} MiB"]){ToolTipText=file.Path});
                    }
                }
                finally{list.EndUpdate();}
                added+=scan.Files.Count;duplicates+=scan.AlreadyQueued;unreadable+=scan.Unreadable;
            }
            status.Text=added==0?"No new install packages found.":$"Added {added} package(s).";
            if(duplicates>0)status.Text+=$" Already queued: {duplicates}.";
            if(unreadable>0)status.Text+=$" Unreadable items skipped: {unreadable}.";
        }
        catch(OperationCanceledException){status.Text="Folder scan cancelled.";}
        catch(Exception e){status.Text="Could not finish adding packages. "+e.Message;}
        finally
        {
            drops.Clear();discovery.Dispose();discovery=null;LockControls();
            if(closing)BeginInvoke(Close);
        }
    }
    void Remove(){foreach(int i in list.SelectedIndices.Cast<int>().OrderDescending()){list.Items.RemoveAt(i);queue.RemoveAt(i);}Ready();}
    void Ready(){
        queueEmpty.Visible=queue.Count==0;list.Visible=queue.Count>0;
        removeQueued.Enabled=CanQueue && discovery==null && list.SelectedIndices.Count!=0;
        bar.Visible=active!=null || (queue.Count>0 && bar.Completed);
        if(scanSavings!=null)scanSavings.Enabled=CanQueue&&discovery==null&&keys!=null&&queue.Count>0;
        if(go!=null)go.Enabled=CanQueue && discovery==null && keys!=null && drives.SelectedItem is Volume && queue.Count>0;
    }
}
