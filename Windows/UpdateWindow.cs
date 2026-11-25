using Hats.Core;

namespace Nexus.Desktop;

partial class LibraryWindow
{
    readonly ToolStripMenuItem updateCommand = new("Check for &updates…");
    readonly CancellationTokenSource updateLifetime = new();
    DesktopUpdate? pendingUpdate;
    bool checkingUpdate;

    bool CanOfferUpdate => !closing && !IsDisposed && Enabled && ActiveForm == this &&
        active == null && discovery == null && !library.IsWriting && !ToolBusy;

    async Task CheckDesktopUpdate(bool manual)
    {
        if (checkingUpdate || closing) return;
        checkingUpdate = true;
        updateCommand.Enabled = false;
        updateCommand.Text = "Checking for updates…";
        try
        {
            using var client = new ReleaseService();
            string json = await client.LatestJsonAsync(DesktopUpdate.Repository,updateLifetime.Token);
            if (IsDisposed || closing) return;
            pendingUpdate = DesktopUpdate.Find(json,DesktopUpdate.Installed);
            if (manual && pendingUpdate == null)
                MessageBox.Show(this,$"You’re running the latest version, {DesktopUpdate.Display(DesktopUpdate.Installed)}.","Nexus Manager for PC");
            else OfferUpdate();
        }
        catch (Exception ex)
        {
            if (manual && !IsDisposed && !closing)
                MessageBox.Show(this,"Could not check for updates. "+ex.Message,"Nexus Manager for PC",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        finally
        {
            checkingUpdate = false;
            if (!IsDisposed) { updateCommand.Text = "Check for &updates…"; updateCommand.Enabled = true; }
        }
    }

    void OfferUpdate()
    {
        if (pendingUpdate is not {} next || !CanOfferUpdate) return;
        pendingUpdate = null;
        string queueNote = queue.Count == 0 ? "" : "\n\nReopening Manager clears the current install queue.";
        if (MessageBox.Show(this,$"Nexus Manager for PC {DesktopUpdate.Display(next.Version)} is available. Download, install and reopen Manager?{queueNote}",
            "Desktop update",MessageBoxButtons.YesNo,MessageBoxIcon.Information) != DialogResult.Yes) return;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NexusManagerPC","updates");
        string file = Path.Combine(folder,Guid.NewGuid().ToString("N")+".exe");
        bool launched = false;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder,"*.exe"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(old),"N",out _) && File.GetLastWriteTimeUtc(old)<DateTime.UtcNow.AddDays(-1))
                    TryDeleteUpdate(old);
            if (!DownloadUpdate(next,file)) return;
            next.Launch(file);
            launched = true;
            closing = true;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this,ex.Message,"Desktop update",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        finally { if (!launched) TryDeleteUpdate(file); }
    }

    bool DownloadUpdate(DesktopUpdate next,string file)
    {
        using var dialog = new Form { Text = "Updating Nexus Manager",StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,ClientSize = new Size(380,110),MaximizeBox = false,MinimizeBox = false,ShowInTaskbar = false };
        var label = new Label { Text = $"Downloading {DesktopUpdate.Display(next.Version)}…",AutoSize = true,Location = new Point(14,12) };
        var progress = new ProgressBar { Location = new Point(14,37),Size = new Size(352,19),Maximum = 100 };
        var stop = new Button { Text = "Cancel",Location = new Point(285,70),AutoSize = true };
        dialog.Controls.AddRange([label,progress,stop]);
        using var cancel = new CancellationTokenSource();
        bool done = false;
        stop.Click += (_,_)=>dialog.Close();
        dialog.FormClosing += (_,e)=>{ if (!done) { e.Cancel = true; cancel.Cancel(); stop.Enabled = false; } };
        Exception? failure = null;
        dialog.Shown += async (_,_)=>{
            try
            {
                using var client = new ReleaseService();
                int previous = -1;
                var meter = new Progress<int>(value=>{if(!done)progress.Value = value;});
                await client.DownloadAsync(next.Installer,file,value=>{
                    int percent = Math.Clamp((int)value,0,100);
                    if(percent != previous) {previous = percent; ((IProgress<int>)meter).Report(percent);}
                },cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                done = true; dialog.DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) { done = true; dialog.DialogResult = DialogResult.Cancel; }
            catch (Exception ex) { failure = ex; done = true; dialog.DialogResult = DialogResult.Cancel; }
        };
        var result = dialog.ShowDialog(this);
        if (failure != null) throw failure;
        return result == DialogResult.OK;
    }

    static void TryDeleteUpdate(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
