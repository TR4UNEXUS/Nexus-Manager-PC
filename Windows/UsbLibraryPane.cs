using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Nexus.Pc;

namespace Nexus.Desktop;

sealed class UsbLibraryPane : UserControl
{
    readonly ListView covers=new NexusTable{Name="InstalledGames",AccessibleName="Installed games",Dock=DockStyle.Fill,
        View=View.Details,FullRowSelect=true,MultiSelect=true,HideSelection=false,ShowItemToolTips=true,UseCompatibleStateImageBehavior=false};
    readonly ListView parts=new NexusTable{Name="GameContents",AccessibleName="Game contents",Dock=DockStyle.Fill,
        View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,ShowItemToolTips=true};
    readonly Label details=new(){Text="Game details",UseMnemonic=false,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
    readonly Label summary=new(){Name="SelectedGame",Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
    readonly Label status=new(){Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
    readonly WorkspaceEmpty empty=new(WorkspaceIcon.Library);
    readonly PictureBox selectedCover=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Margin=new Padding(4,2,8,2),AccessibleName="Selected game cover"};
    readonly SplitContainer split=new(){Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,Size=new Size(800,400),Panel1MinSize=80,Panel2MinSize=85,SplitterWidth=5};
    readonly OperationProgressBar bar=new();
    readonly Button refresh,remove,recover,compress,scanSavings;
    readonly Dictionary<ulong,CompressionEstimate> estimates=[];
    CancellationTokenSource? savingsScan;
    Task savingsTask=Task.CompletedTask;
    // Reuse complete results when switching tabs. Content IDs identify the installed version.
    readonly Dictionary<(string Root,uint Serial,string Keys,string Content),CompressionEstimate> sessionEstimates=[];
    static string ContentStamp(Family game)=>string.Join(",",game.Members.Select(m=>m.MetaId));

    Volume? volume;string? keyPath;Inventory? inventory;
    CancellationTokenSource? scan,write;
    bool compressing;
    int sortColumn;bool descending;
    public bool IsWriting=>write!=null;
    public event Action? WriteStateChanged;

    public UsbLibraryPane(){
        Dock=DockStyle.Fill;
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(6)};
        grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(grid);
        var content=new Panel{Dock=DockStyle.Fill};content.Controls.Add(split);content.Controls.Add(empty);grid.Controls.Add(content,0,0);
        foreach(var label in new[]{"Game","Installed version","Updates","DLC","USB size","Compression","Est. saving","File check"})covers.Columns.Add(label);
        foreach(var label in new[]{"Content","Display version","Content version","Title ID","USB size","File check"})parts.Columns.Add(label);
        split.Panel1.Controls.Add(covers);
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Padding=new Padding(2,4,2,0)};
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,62));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        details.Font=new Font(Font.FontFamily,12,FontStyle.Bold);
        body.RowStyles.Add(new RowStyle(SizeType.Absolute,details.Font.Height+9));body.RowStyles.Add(new RowStyle(SizeType.Absolute,Math.Max(28,Font.Height+10)));body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        split.Panel2.Controls.Add(body);body.Controls.Add(selectedCover,0,0);body.SetRowSpan(selectedCover,2);
        body.Controls.Add(details,1,0);body.Controls.Add(summary,1,1);body.Controls.Add(parts,0,2);body.SetColumnSpan(parts,2);
        if(NexusTheme.Enabled){
            BackColor=NexusTheme.Background;ForeColor=NexusTheme.Text;split.BackColor=NexusTheme.Edge;
            body.BackColor=NexusTheme.Background;summary.ForeColor=status.ForeColor=NexusTheme.Secondary;
        }
        var tools=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=6,RowCount=3};
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));for(int i=0;i<5;i++)tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Controls.Add(tools,0,1);
        refresh=LibraryWindow.ButtonFor("Refresh",()=>{sessionEstimates.Clear();bar.Clear();Reload();});remove=LibraryWindow.ButtonFor("Delete selected…",async()=>await Change(false));
        recover=LibraryWindow.ButtonFor("Recover library",async()=>await Change(true));recover.Visible=false;
        compress=LibraryWindow.ButtonFor("Compress game…",async()=>{if(compressing)write?.Cancel();else await CompressGame();});
        scanSavings=LibraryWindow.ButtonFor("Scan savings",()=>{if(savingsScan!=null)savingsScan.Cancel();else StartSavings(automatic:false);});
        tools.RowStyles.Add(new RowStyle(SizeType.AutoSize));tools.RowStyles.Add(new RowStyle(SizeType.AutoSize));tools.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.Height=Font.Height+6;status.MinimumSize=new Size(0,Font.Height+6);
        tools.Controls.Add(status,0,0);tools.SetColumnSpan(status,6);
        tools.Controls.Add(bar,0,1);tools.SetColumnSpan(bar,6);
        tools.Controls.Add(recover,1,2);tools.Controls.Add(refresh,2,2);tools.Controls.Add(scanSavings,3,2);tools.Controls.Add(compress,4,2);tools.Controls.Add(remove,5,2);
        covers.SelectedIndexChanged+=(_,_)=>ShowSelection();
        covers.ColumnClick+=(_,e)=>{descending=e.Column==sortColumn&&!descending;sortColumn=e.Column;SortGames();};
        covers.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Delete && remove.Enabled){e.Handled=true;remove.PerformClick();}};
        parts.ItemActivate+=(_,_)=>{
            if(parts.SelectedItems.Count==1 && parts.SelectedItems[0].Tag is Member m && m.Issues.Count>0)
                MessageBox.Show(this,string.Join("\n",m.Issues),"File check",MessageBoxButtons.OK,MessageBoxIcon.Information);
        };
        void Widths(ListView view,int flexible,int[] widths){
            if(view.ClientSize.Width<=0)return;
            int available=view.ClientSize.Width;
            for(int i=0;i<widths.Length;i++)if(i!=flexible){view.Columns[i].Width=LogicalToDeviceUnits(widths[i]);available-=view.Columns[i].Width;}
            view.Columns[flexible].Width=Math.Max(LogicalToDeviceUnits(115),available);
        }
        covers.Resize+=(_,_)=>Widths(covers,0,[0,98,63,45,78,115,148,94]);
        parts.Resize+=(_,_)=>Widths(parts,0,[0,120,112,165,94,175]);
        double fraction=.56;bool sizing=false;
        split.SplitterMoved+=(_,_)=>{if(!sizing && split.Height>0)fraction=(double)split.SplitterDistance/split.Height;};
        split.SizeChanged+=(_,_)=>{
            if(split.Height<=split.Panel1MinSize+split.Panel2MinSize+split.SplitterWidth)return;
            sizing=true;try{split.SplitterDistance=Math.Clamp((int)(fraction*split.Height),split.Panel1MinSize,split.Height-split.Panel2MinSize-split.SplitterWidth);}finally{sizing=false;}
        };
        SetSource(null,null,true);
    }
    public void SetSource(Volume? drive,string? keys,bool force=false){
        if(IsWriting)return;
        if(!force && volume?.Root==drive?.Root && volume?.Serial==drive?.Serial && keyPath==keys)return;
        if(force)sessionEstimates.Clear();
        bar.Clear();volume=drive;keyPath=keys;Reload();
    }
    Task StopSavings(){
        var current=savingsScan;
        savingsScan=null;
        current?.Cancel();
        return savingsTask;
    }
    public Task StopReading(){scan?.Cancel();return StopSavings();}
    public void Cancel(){write?.Cancel();_ = StopReading();}

    protected override void Dispose(bool disposing){
        if(disposing){Cancel();var images=covers.SmallImageList;covers.SmallImageList=null;images?.Dispose();selectedCover.Image?.Dispose();}
        base.Dispose(disposing);
    }
    void SetActions(){
        bool idle=write==null && scan==null;
        refresh.Enabled=idle && volume!=null && keyPath!=null;
        recover.Enabled=idle && recover.Visible;
        remove.Enabled=idle && !recover.Visible && inventory?.Unidentified==0 && covers.SelectedItems.Count>0 &&
            covers.SelectedItems.Cast<ListViewItem>().All(row=>row.Tag is Family g && g.Complete && (g.Id>>56)==1);
        scanSavings.Text=savingsScan!=null?"Cancel scan":"Scan savings";
        scanSavings.Enabled=savingsScan!=null||(idle&&!recover.Visible&&inventory?.Games.Count>0);
        compress.Text=compressing?"Cancel compression":"Compress game…";
        compress.Enabled=compressing || (idle && !recover.Visible && covers.SelectedItems.Count==1 &&
            covers.SelectedItems[0].Tag is Family selected && selected.Complete && (selected.Id>>56)==1);
    }
    void Clear(){
        inventory=null;estimates.Clear();covers.Items.Clear();var old=covers.SmallImageList;covers.SmallImageList=null;old?.Dispose();recover.Visible=false;ShowSelection();
    }
    async Task CompressGame(){
        if(!compress.Enabled || volume is not { } target || keyPath is not { } keys ||
            covers.SelectedItems.Count!=1 || covers.SelectedItems[0].Tag is not Family game)return;
        compressing=true;write=new();var token=write.Token;SetActions();WriteStateChanged?.Invoke();
        string result="Compression stopped.";bar.Begin();
        try{
            await StopSavings();token.ThrowIfCancellationRequested();
            using var job=new CompressionJob();target.Check();
            var keyset=RememberedKeys.Current.Open(keys);
            var permissions=new Dictionary<string,Permission>();
            var note=new Progress<string>(message=>{if(write!=null)status.Text=message;});
            if(!estimates.TryGetValue(game.Id,out var estimate)||estimate.BaseOnly){
                status.Text="Scanning "+game.Name+"...";
                var previewMeter=new Progress<CompressionUpdate>(tick=>{if(compressing){status.Text=tick.Stage;bar.Report(tick.Fraction*.05);}});
                var entries=await WithPermissions(()=>Compression.ReadEntries(target.Root,game,keyset,token,target.Check,permissions),
                    keys,permissions,token);
                estimate=await EstimateGame(game,target,keys,entries,false,note,token,previewMeter);
                RememberEstimate(game,estimate);
            }
            token.ThrowIfCancellationRequested();target.Check();
            if(estimate.Skipped||estimate.AlreadyAtSize){
                result=estimate.Skipped?"The compressor skips this game.":"This game is already at its compressed size.";
                CompressionPreview.ShowResults(this,[estimate]);return;
            }
            string confirm=$"{game.Name}\n\n{estimate.Description}\n\nCompress and replace the USB copy on {target.Root}? Open Switch Manager 0.2.39 or later afterwards to sync HOME.";
            if(MessageBox.Show(this,confirm,"Compress installed game",MessageBoxButtons.YesNo,MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2)!=DialogResult.Yes){result="Compression cancelled.";return;}
            status.Text="Preparing "+game.Name+"...";
            var snapshotMeter=new Progress<double>(f=>{if(compressing)bar.Report(.05+.12*f);});
            var source=await WithPermissions(()=>{
                Compression.Snapshot(target.Root,game,keyset,job.Source,token,target.Check,permissions,snapshotMeter);
                return Packages.Inspect(job.Source,keyset,token);
            },keys,permissions,token);
            var converting=new Progress<CompressionUpdate>(tick=>{if(compressing){
                status.Text=tick.Stage;bar.Report(.17+.61*tick.Fraction);
            }});
            var output=await job.Convert([source],keys,note,token,converting);
            if(output.Count!=1)throw new InvalidDataException("The compressor did not produce one complete game package.");
            status.Text="Installing the compressed game on USB...";
            var progress=new Progress<Activity>(a=>{if(compressing){
                bar.Report(.78+.21*a.Done/Math.Max(1d,a.Total));
                status.Text=a.Done>=a.Total?"Finishing USB replacement…":$"{a.Stage} · {a.MiBPerSecond:0.#} MiB/s";
            }});
            long saved=await Task.Run(()=>CompressedReplacement.Install(target.Root,game,keyset,output[0],progress,token,target.Check),token);
            bar.Report(1);result="Compressed "+game.Name+" and freed "+FormatSize(saved)+". Open Switch Manager 0.2.39 or later to sync HOME.";
        }catch(OperationCanceledException){result="Compression stopped.";}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Compress game",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{if(!bar.Completed)bar.Clear();compressing=false;write.Dispose();write=null;WriteStateChanged?.Invoke();if(!IsDisposed)Reload(result);}
    }
    async Task<T> WithPermissions<T>(Func<T> read,string keys,Dictionary<string,Permission> permissions,CancellationToken token){
        while(true){
            try{return await Task.Run(read,token);}
            catch(CompressionPermissionNeeded ex){
                using var originals=new OpenFileDialog{Title=ex.Message,Multiselect=true,
                    Filter="Original game, update or DLC package|*.nsp;*.nsz;*.nsx;*.xci;*.xcz"};
                if(originals.ShowDialog(this)!=DialogResult.OK)throw new OperationCanceledException();
                var paths=originals.FileNames;
                var supplied=await Task.Run(()=>{
                    var keyset=RememberedKeys.Current.Open(keys);
                    return paths.SelectMany(path=>Packages.Inspect(path,keyset,token).Tickets).ToList();
                },token);
                if(permissions.ContainsKey(ex.RightsId)||!supplied.Any(t=>t.Id==ex.RightsId))
                    throw new IOException("These packages do not contain the required permission.");
                foreach(var ticket in supplied)permissions[ticket.Id]=ticket;
            }
        }
    }
    static async Task<CompressionEstimate> EstimateGame(Family game,Volume target,string keys,List<Compression.Entry> entries,
        bool baseOnly,IProgress<string> note,CancellationToken token,IProgress<CompressionUpdate> meter){
        token.ThrowIfCancellationRequested();target.Check();
        using var job=new CompressionJob();
        var rows=await job.Estimate([],keys,note,token,entries,meter);
        token.ThrowIfCancellationRequested();target.Check();
        if(rows.Count!=1||rows[0].GameId!=game.Id)throw new IOException("The game changed during the scan. Refresh the USB library.");
        var estimate=rows[0];
        long before=baseOnly?entries.Where(e=>e.Path!=null).Sum(e=>e.Length):game.Bytes;
        return estimate with {Name=game.Name,BeforeBytes=before,BaseOnly=baseOnly,
            AfterBytes=estimate.Skipped||estimate.AlreadyAtSize?before:estimate.AfterBytes};
    }
    void RememberEstimate(Family game,CompressionEstimate estimate){
        estimates[game.Id]=estimate;
        var row=covers.Items.Cast<ListViewItem>().First(r=>r.Tag is Family g&&g.Id==game.Id);
        row.SubItems[6].Text=estimate.RowSavings;
        row.ToolTipText=estimate.Description;
        if(volume!=null&&keyPath!=null){
            var cacheKey=(volume.Root,volume.Serial,keyPath,ContentStamp(game));
            if(estimate.BaseOnly)sessionEstimates.Remove(cacheKey);
            else sessionEstimates[cacheKey]=estimate;
        }
    }
    void StartSavings(bool automatic){
        if(write!=null||scan!=null||savingsScan!=null||recover.Visible||volume is not {} target||keyPath is not {} keys)return;
        var rows=covers.Items.Cast<ListViewItem>().Where(row=>automatic
            ? !estimates.ContainsKey(((Family)row.Tag!).Id)
            : row.Selected||covers.SelectedItems.Count==0).ToArray();
        if(rows.Length==0)return;
        var current=new CancellationTokenSource();savingsScan=current;SetActions();
        savingsTask=ScanSavings(rows,target,keys,automatic,current,savingsTask);
    }
    async Task ScanSavings(ListViewItem[] rows,Volume target,string keys,bool automatic,CancellationTokenSource current,Task previous){
        var token=current.Token;
        bool Active()=>savingsScan==current&&!token.IsCancellationRequested&&!IsDisposed;
        int scanned=0,failed=0;
        try{
            await previous;token.ThrowIfCancellationRequested();bar.Begin();
            var keyset=await Task.Run(()=>RememberedKeys.Current.Open(keys),token);
            foreach(var row in rows){
                token.ThrowIfCancellationRequested();target.Check();
                var game=(Family)row.Tag!;
                int index=scanned+failed;
                status.Text=$"Scanning savings {index+1} of {rows.Length} · {game.Name}";
                var note=new Progress<string>(message=>{if(Active()&&message.StartsWith("Warning:",StringComparison.Ordinal))status.Text=message;});
                var meter=new Progress<CompressionUpdate>(tick=>{if(Active())bar.Report((index+tick.Fraction)/rows.Length);});
                try{
                    var input=await Task.Run(()=>Compression.ReadPreviewEntries(target.Root,game,keyset,token,target.Check),token);
                    var estimate=await EstimateGame(game,target,keys,input.Entries,input.BaseOnly,note,token,meter);
                    if(!Active())return;
                    RememberEstimate(game,estimate);scanned++;
                    if(!automatic&&rows.Length==1)CompressionPreview.ShowResults(this,[estimate]);
                }catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){
                    token.ThrowIfCancellationRequested();target.Check();
                    if(!Active())return;
                    estimates.Remove(game.Id);
                    sessionEstimates.Remove((target.Root,target.Serial,keys,ContentStamp(game)));
                    bool needsPermission=ex is CompressionPermissionNeeded;
                    row.SubItems[6].Text=needsPermission?"Permission unavailable":"Unavailable";
                    row.ToolTipText=needsPermission?"Manager could not read the base game with the permissions saved on this drive.":ex.Message;
                    failed++;
                    if(!automatic&&rows.Length==1)status.Text=row.ToolTipText;
                }
            }
            if(!Active())return;
            if(scanned>0)bar.Report(1);else bar.Clear();
            if(automatic||rows.Length!=1||failed==0)status.Text=$"Savings scan finished · {scanned} estimated"+(failed>0?$" · {failed} unavailable":"");
        }catch(OperationCanceledException){if(savingsScan==current&&!IsDisposed)status.Text="Savings scan cancelled.";}
        catch(Exception ex){if(Active())status.Text=ex.Message;}
        finally{
            if(savingsScan==current){
                savingsScan=null;
                if(!IsDisposed){if(!bar.Completed)bar.Clear();SortGames();ShowSelection();}
            }
            current.Dispose();
        }
    }

    public async void Reload(string? message=null){
        if(IsWriting||IsDisposed)return;
        var priorSavings=StopSavings();scan?.Cancel();scan=null;Clear();
        var target=volume;var keys=keyPath;
        empty.Visible=true;split.Visible=false;
        if(target==null||keys==null){
            empty.SetMessage(target==null?"Connect your USB game drive":"Choose your prod.keys file",
                target==null?"Select a connected drive above to view its games.":"Use Choose prod.keys above to read the games on this drive.");
            status.Text="";SetActions();return;
        }
        empty.SetMessage("Reading your library",target.Root);
        var current=new CancellationTokenSource();scan=current;SetActions();status.Text="Reading games on "+target.Root+"…";
        try{
            await priorSavings;current.Token.ThrowIfCancellationRequested();
            var result=await Task.Run(()=>{
                target.Check();Library.RecoverInstallIfNeeded(target.Root,target.Check);var data=Library.Scan(target.Root,RememberedKeys.Current.Open(keys),current.Token);
                bool pending=Library.NeedsRecovery(target.Root);target.Check();return (data,pending);
            },current.Token);
            if(IsDisposed||current!=scan||current.IsCancellationRequested)return;
            inventory=result.data;recover.Visible=result.pending;Render(inventory);
            bool hasGames=inventory.Games.Count!=0;split.Visible=hasGames;empty.Visible=!hasGames;
            if(!hasGames)empty.SetMessage("No games listed",inventory.Unidentified>0?"This drive contains files Manager could not identify.":"Use Install games to add packages to this drive.");
            status.Text=message??(result.pending?"An interrupted deletion needs recovery.":inventory.Unidentified>0?
                $"{inventory.Games.Count} games · {inventory.Unidentified} unidentified files · deletion unavailable":
                inventory.Games.Count==0?"No installed games found on this USB drive.":$"{inventory.Games.Count} games on {target.Root}");
        }catch(OperationCanceledException){}
        catch(Exception ex){if(current==scan&&!IsDisposed){Clear();empty.SetMessage("Could not load this library","Check the drive and key file, then choose Refresh.");status.Text=ex.Message;}}
        finally{
            if(current==scan){
                scan=null;
                if(!IsDisposed&&!current.IsCancellationRequested){SetActions();StartSavings(automatic:true);}
            }
            current.Dispose();
        }
    }
    void Render(Inventory data){
        int side=Math.Clamp(LogicalToDeviceUnits(20),16,64);
        var icons=new ImageList{ColorDepth=ColorDepth.Depth32Bit,ImageSize=new Size(side,side)};
        _=icons.Handle;
        using(var generic=SystemIcons.Application.ToBitmap())icons.Images.Add(generic);
        covers.BeginUpdate();
        try{
            covers.SmallImageList=icons;
            foreach(var game in data.Games){
                int image=0;
                if(game.Icon!=null && Thumbnail(game.Icon,side) is Bitmap bitmap){
                    using(bitmap){image=icons.Images.Count;icons.Images.Add(bitmap);}
                }
                var row=new ListViewItem([game.Name,CurrentVersion(game),Updates(game).ToString(),Dlc(game).ToString(),FormatSize(game.Bytes),CompressionLabel(game),"—",game.Complete?"Files present":"Needs checking"],image){Tag=game,ToolTipText=game.Name+" · "+game.Id.ToString("X16")};
                covers.Items.Add(row);
                if(game.Complete&&volume!=null&&keyPath!=null&&sessionEstimates.TryGetValue((volume.Root,volume.Serial,keyPath,ContentStamp(game)),out var saved)){
                    estimates[game.Id]=saved;row.SubItems[6].Text=saved.RowSavings;row.ToolTipText=saved.Description;
                }
            }
        }finally{covers.EndUpdate();}
        SortGames();if(covers.Items.Count>0)covers.Items[0].Selected=true;
        ShowSelection();
    }
    static Bitmap? Thumbnail(byte[] data,int side){
        // Control icons are JPEGs. Bound decoded dimensions before invoking the native decoder.
        if(!JpegSizeAllowed(data))return null;
        try{
            using var source=new MemoryStream(data,false);using var image=Image.FromStream(source,false,true);
            var thumb=new Bitmap(side,side);using var g=Graphics.FromImage(thumb);
            g.Clear(Color.Transparent);g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            g.DrawImage(image,new Rectangle(0,0,side,side));return thumb;
        }catch(Exception ex) when(ex is ArgumentException or OutOfMemoryException or ExternalException){return null;}
    }
    static bool JpegSizeAllowed(ReadOnlySpan<byte> bytes){
        if(bytes.Length<4 || bytes[0]!=255||bytes[1]!=216)return false;
        for(int start=2;start+4<=bytes.Length;){
            if(bytes[start++]!=255)return false;
            while(start<bytes.Length && bytes[start]==255)start++;
            if(start+3>bytes.Length)return false;
            int marker=bytes[start++],length=(bytes[start]<<8)|bytes[start+1];
            if(length<2 || length>bytes.Length-start)return false;
            if(marker is 0xc0 or 0xc1 or 0xc2){
                if(length<8)return false;
                int h=(bytes[start+3]<<8)|bytes[start+4],w=(bytes[start+5]<<8)|bytes[start+6];
                return w is >0 and <=2048 && h is >0 and <=2048;
            }
            if(marker is 0xda or 0xd9)return false;
            start+=length;
        }
        return false;
    }
    static string FormatSize(long bytes)=>bytes>=1073741824?$"{bytes/1073741824d:0.##} GiB":$"{bytes/1048576d:0.##} MiB";
    static int Updates(Family g)=>g.Members.Count(m=>m.Metadata.Type==0x81);
    static int Dlc(Family g)=>g.Members.Where(m=>m.Metadata.Type==0x82).Select(m=>m.Metadata.Game.Id).Distinct().Count();
    static Member Latest(Family g)=>g.Members.OrderBy(m=>m.Metadata.Type==0x81?0:m.Metadata.Type==0x80?1:2)
        .ThenByDescending(m=>m.Metadata.Game.Version).First();
    static string CurrentVersion(Family g){var m=Latest(g);return m.DisplayVersion??$"v{m.Metadata.Game.Version}";}
    static string CompressionLabel(Family game)=>game.Compression switch {
        CompressionState.NexusCompressed=>"Nexus compressed",CompressionState.NexusBase=>"Nexus base",
        CompressionState.Compressed=>"Compressed",CompressionState.CompressedBase=>"Compressed base",
        CompressionState.Rebuilt=>"Compressor mark",CompressionState.CompressedData=>"Compressed data",
        CompressionState.NoMarker=>"No marker",_=>"Unknown"
    };
    sealed class GameOrder(int column,bool reverse,Dictionary<ulong,CompressionEstimate> estimates):System.Collections.IComparer {
        public int Compare(object? x,object? y){
            if(x is not ListViewItem { Tag: Family a }||y is not ListViewItem { Tag: Family b })return 0;
            int decision=column switch{
                1=>Latest(a).Metadata.Game.Version.CompareTo(Latest(b).Metadata.Game.Version),
                2=>Updates(a).CompareTo(Updates(b)),3=>Dlc(a).CompareTo(Dlc(b)),4=>a.Bytes.CompareTo(b.Bytes),
                5=>string.Compare(CompressionLabel(a),CompressionLabel(b),StringComparison.CurrentCultureIgnoreCase),
                6=>Nullable.Compare(estimates.GetValueOrDefault(a.Id)?.Percent,estimates.GetValueOrDefault(b.Id)?.Percent),
                7=>a.Complete.CompareTo(b.Complete),_=>0};
            if(decision==0)decision=StringComparer.CurrentCultureIgnoreCase.Compare(a.Name,b.Name);
            return reverse?-Math.Sign(decision):Math.Sign(decision);
        }
    }
    void SortGames(){covers.ListViewItemSorter=new GameOrder(sortColumn,descending,estimates);}
    void ShowSelection(){
        parts.BeginUpdate();
        try{
            parts.Items.Clear();var selected=covers.SelectedItems;
            var oldCover=selectedCover.Image;selectedCover.Image=null;oldCover?.Dispose();
            if(selected.Count!=1||selected[0].Tag is not Family g){
                details.Text="Game details";summary.Text=selected.Count>1?$"{selected.Count} games selected":"Select a game to see its base game, updates and DLC.";return;
            }
            details.Text=g.Name;
            if(g.Icon!=null)selectedCover.Image=Thumbnail(g.Icon,Math.Clamp(LogicalToDeviceUnits(48),32,112));
            summary.UseMnemonic=false;
            int updates=Updates(g);
            summary.Text=$"Title ID  {g.Id:X16}     ·     {FormatSize(g.Bytes)} on USB     ·     {updates} {(updates==1?"update":"updates")}     ·     {Dlc(g)} DLC";
            foreach(var m in g.Members.OrderBy(m=>m.Metadata.Type).ThenBy(m=>m.Metadata.Game.Id).ThenByDescending(m=>m.Metadata.Game.Version)){
                string kind=m.Metadata.Type==0x84?"DLC update":m.Metadata.Game.Kind;
                var check=m.Issues.Count==0?"Files present":$"{m.Issues.Count} file issue(s) · open for details";
                var row=new ListViewItem([kind,m.DisplayVersion??"—",$"v{m.Metadata.Game.Version}",m.Metadata.Game.Id.ToString("X16"),FormatSize(m.Bytes),check])
                    {Tag=m,ToolTipText=string.Join("\n",m.Issues)};
                parts.Items.Add(row);
            }
        }finally{parts.EndUpdate();SetActions();}
    }
    async Task Change(bool recovery){
        if(write!=null||scan!=null||volume is not Volume target||keyPath is not string keys)return;
        var selected=covers.SelectedItems.Cast<ListViewItem>().Select(i=>(Family)i.Tag!).ToList();
        if(!recovery&&(!remove.Enabled||selected.Count==0))return;
        var question=recovery?"Recover the interrupted USB deletion?":
            "Permanently remove the USB files for these games, including their updates and DLC?\n\n"+
            string.Join("\n",selected.Take(10).Select(g=>g.Name))+(selected.Count>10?$"\n… and {selected.Count-10} more":"")+
            "\n\nOpen Switch Manager 0.2.38 or later with this drive connected. It will update HOME and remove unused tickets for these games. Shared files, other installed copies and saved progress are kept.";
        if(MessageBox.Show(this,question,recovery?"Recover library":"Delete USB games",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        write=new();var token=write.Token;SetActions();WriteStateChanged?.Invoke();string? result=null;
        try{
            await StopSavings();token.ThrowIfCancellationRequested();
            if(recovery)await Task.Run(()=>Library.RecoverInterrupted(target.Root,target.Check),token);
            else{
                var keyset=RememberedKeys.Current.Open(keys);long freed=0;
                foreach(var game in selected){status.Text="Removing "+game.Name+"…";freed+=(await Task.Run(()=>Library.Delete(target.Root,keyset,game,token,target.Check),token)).Bytes;}
                result=$"Removed {selected.Count} game(s) · {FormatSize(freed)} freed";
            }
        }catch(OperationCanceledException){result="Stopped. Review the remaining games.";}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"USB library",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{write.Dispose();write=null;WriteStateChanged?.Invoke();if(!IsDisposed)Reload(result);}
    }
}
