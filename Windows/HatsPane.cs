using Hats.Core;
using Hats.WinUI;

namespace Nexus.Desktop;

internal sealed class HatsPane : UserControl
{
    readonly ComboBox packs = Choice(), cards = Choice();
    readonly Label releaseName = new() {Text="HATS pack",AutoSize=true};
    readonly Label status = new() {Text="Choose a pack and the Switch SD card.", AutoSize=true};
    readonly Label destination = new() {AutoSize=true};
    readonly TextBox log = new() {Dock=DockStyle.Fill, ReadOnly=true, Multiline=true, ScrollBars=ScrollBars.Vertical};
    readonly ProgressBar progress = new(){Dock=DockStyle.Top, Height=12};
    readonly Button refresh, chooseBackup, install, cancel, erase;
    readonly AppSettings settings = AppSettings.Load();
    HatsRelease? release;
    CancellationTokenSource? job;
    readonly CancellationTokenSource lifetime = new();
    bool loading, opened;
    public bool Busy => job != null;
    public event Action? BusyChanged;
    static ComboBox Choice() => new(){Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};
    static FlowLayoutPanel Row() => new(){Dock=DockStyle.Top,AutoSize=true,WrapContents=true};

    public HatsPane()
    {
        Dock=DockStyle.Fill;
        var layout = new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=1,RowCount=9};
        for(int i=0;i<8;i++)layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent,100));
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        Controls.Add(layout);
        layout.Controls.Add(releaseName,0,0);
        layout.Controls.Add(packs,0,1);
        layout.Controls.Add(new Label{Text="Switch SD card",AutoSize=true,Margin=new Padding(3,10,3,3)},0,2);
        layout.Controls.Add(cards,0,3);
        var backup=Row();
        chooseBackup=LibraryWindow.ButtonFor("Backup folder…",PickBackup);
        destination.Anchor=AnchorStyles.Left;destination.Text=settings.BackupFolder;
        backup.Controls.AddRange([chooseBackup,destination]);layout.Controls.Add(backup,0,4);
        var buttons=Row();
        refresh=LibraryWindow.ButtonFor("Refresh packs and cards",async()=>await RefreshAsync());
        install=LibraryWindow.ButtonFor("Install selected pack",async()=>await ApplyAsync(false));
        erase=LibraryWindow.ButtonFor("Format SD as FAT32…",async()=>await ApplyAsync(true));
        cancel=LibraryWindow.ButtonFor("Cancel",()=>job?.Cancel());
        buttons.Controls.AddRange([install,refresh,LibraryWindow.ButtonFor("Protected files",ShowProtection),erase,cancel]);
        layout.Controls.Add(buttons,0,5);layout.Controls.Add(status,0,6);
        layout.Controls.Add(progress,0,7);layout.Controls.Add(log,0,8);
        if(NexusTheme.Enabled)
        {
            BackColor=NexusTheme.Surface;ForeColor=NexusTheme.Text;
            foreach(Control c in new Control[]{packs,cards,log}){c.BackColor=NexusTheme.Header;c.ForeColor=NexusTheme.Text;}
        }
        cards.SelectedIndexChanged+=(_,_)=>UpdateButtons();
        packs.SelectedIndexChanged+=(_,_)=>UpdateButtons();
        UpdateButtons();
    }
    public async void OpenTab()
    {
        if(opened)return;
        opened=true; await RefreshAsync();
    }
    void UpdateButtons()
    {
        var idle=!Busy&&!loading;
        packs.Enabled=cards.Enabled=refresh.Enabled=chooseBackup.Enabled=idle;
        install.Enabled=idle&&release!=null&&packs.SelectedItem is PackAsset&&cards.SelectedItem is SdVolume;
        erase.Enabled=idle&&cards.SelectedItem is SdVolume;
        if(!Busy)cancel.Enabled=false;
    }
    void Log(string message)
    {
        AppSettings.Log(message);
        log.AppendText(message+Environment.NewLine);
    }
    void ShowProtection() => MessageBox.Show(this,
        "The installer preserves game content, mods, USB mappings and Manager settings. It backs up files it replaces to the folder shown here. Formatting erases the entire card.",
        "HATS install protection",MessageBoxButtons.OK,MessageBoxIcon.Information);
    void PickBackup()
    {
        using var dialog=new FolderBrowserDialog{Description="Choose where to keep SD card backups",SelectedPath=settings.BackupFolder};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var before=settings.BackupFolder;
        try{settings.BackupFolder=dialog.SelectedPath;settings.Save();destination.Text=settings.BackupFolder;}
        catch(Exception e){settings.BackupFolder=before;status.Text=e.Message;}
    }
    async Task RefreshAsync()
    {
        if(Busy||loading)return;
        loading=true;
        var previousCard=cards.SelectedItem as SdVolume; var previousPack=packs.SelectedItem as PackAsset;
        release=null;packs.Items.Clear();cards.Items.Clear();UpdateButtons();status.Text="Looking for SD cards and release packs…";
        try
        {
            var detected=await Task.Run(WindowsVolumes.Scan,lifetime.Token);
            if(IsDisposed)return;
            cards.Items.AddRange(detected);
            cards.SelectedItem=detected.FirstOrDefault(v=>v.Root==previousCard?.Root&&v.Identity==previousCard.Identity);
            if(cards.SelectedIndex<0&&detected.Length==1)cards.SelectedIndex=0;
            using var service=new ReleaseService();
            var latest=await service.LatestAsync(lifetime.Token);
            if(IsDisposed)return;
            release=latest;releaseName.Text="HATS pack  ·  "+latest.Tag;
            packs.Items.AddRange(latest.Assets);packs.SelectedItem=latest.Assets.FirstOrDefault(p=>p.Id==previousPack?.Id);
            if(packs.SelectedIndex<0&&packs.Items.Count>0)packs.SelectedIndex=0;
            status.Text=detected.Length==0?"Connect your Switch SD card and refresh.":"Choose your pack and SD card.";
        }
        catch(Exception e){if(!IsDisposed&&!lifetime.IsCancellationRequested){status.Text="Refresh failed. "+e.Message;Log(status.Text);}}
        finally{loading=false;if(!IsDisposed)UpdateButtons();}
    }
    async Task ApplyAsync(bool format)
    {
        if(Busy||loading||cards.SelectedItem is not SdVolume card)return;
        var pack=packs.SelectedItem as PackAsset;var selectedRelease=release;
        if(!format&&(pack==null||selectedRelease==null))return;
        try
        {
            WindowsVolumes.Validate(card);
            var backup=Path.Combine(settings.BackupFolder,"HATS-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
            if(!format&&Paths.Overlap(backup,card.Root))throw new IOException("Choose a backup folder outside the selected SD card.");
            string prompt=format?$"Erase every file on {card} and format it as FAT32?":$"Install {pack!.Name} at the root of {card.Root}?\n\nBackup folder\n{backup}";
            if(MessageBox.Show(this,prompt,format?"Erase SD card":"Install HATS",MessageBoxButtons.OKCancel,
                format?MessageBoxIcon.Warning:MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
            WindowsVolumes.Validate(card);
            job=new();var token=job.Token;
            UpdateButtons();cancel.Enabled=!format;BusyChanged?.Invoke();progress.Value=0;
            var ui=SynchronizationContext.Current!;
            void Report(JobProgress p) => ui.Post(_=>{if(!Busy)return;status.Text=p.Phase;progress.Value=(int)Math.Clamp(p.Percent,0,100);cancel.Enabled=p.CanCancel;},null);
            Log(format?"Preparing FAT32 format on "+card:"Installing "+pack!.Name+" on "+card);
            if(format)await FormatElevation.RunAsync(card,p=>Report(new("Formatting FAT32",p,false)));
            else await Task.Run(async() =>
            {
                var preserve=InstallPolicy.LoadPreserved(AppSettings.ProtectionFile);
                using var guard=new Semaphore(1,1,@"Local\HATS-Volume-"+card.Identity.Serial.ToString("X8"));
                if(!guard.WaitOne(0))throw new IOException("Another HATS operation is using this card.");
                try{await new Hats.Core.Installer().RunAsync(selectedRelease!,pack!,card.Root,backup,preserve,
                    m=>ui.Post(_=>Log(m),null),Report,token,()=>WindowsVolumes.Validate(card));}
                finally{guard.Release();}
            },token);
            progress.Value=100;status.Text=format?"Format complete. Safely eject and reconnect the card.":"HATS installed. You can safely eject the SD card.";
            Log(status.Text);
            if(format)cards.Items.Clear();
        }
        catch(OperationCanceledException){status.Text="Operation cancelled.";Log(status.Text);}
        catch(Exception e){status.Text="Operation stopped. "+e.Message;Log(status.Text);}
        finally{job?.Dispose();job=null;cancel.Enabled=false;UpdateButtons();BusyChanged?.Invoke();}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){lifetime.Cancel();lifetime.Dispose();}
        base.Dispose(disposing);
    }
}
