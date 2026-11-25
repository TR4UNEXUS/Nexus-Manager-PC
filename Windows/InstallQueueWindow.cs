using Nexus.Pc;

namespace Nexus.Desktop;

partial class LibraryWindow
{
    const string SwitchImport = "Safely eject the drive in Windows and connect it to your Switch. On Nexus HATS NX1, open Switch Manager 0.2.40 or later to pick up changes. You can also choose Installed games → Import from PC. Fully restart the Switch after import.";
    int feedbackId;

    IProgress<T> Feedback<T>(int id, Action<T> update) =>
        new Progress<T>(value => { if (active != null && id == feedbackId) update(value); });

    IProgress<CompressionUpdate> CompressionMeter(int id, double start, double span) =>
        Feedback<CompressionUpdate>(id, tick => { status.Text = tick.Stage; bar.Report(start + span * tick.Fraction); });

    async Task<List<CompressionEstimate>> EstimatePackages(List<Package> plans, string keyPath,
        double start, double span, CancellationToken token)
    {
        var games = plans.SelectMany(p => p.Games).ToList();
        Compression.ExpectedTitles(games);
        int id = ++feedbackId;
        status.Text = "Scanning compression savings…";
        using var job = new CompressionJob();
        var estimates = await job.Estimate(plans.Select(p => p.Path), keyPath,
            Feedback<string>(id, s => status.Text = s), token, meter: CompressionMeter(id, start, span));
        if (!estimates.Select(e => e.GameId).ToHashSet().SetEquals(games.Select(g => g.FamilyId)))
            throw new InvalidDataException("The scan could not estimate every selected game.");
        return estimates;
    }

    async Task Run(bool scanOnly = false)
    {
        var target = drives.SelectedItem as Volume;
        var keyPath = keys;
        if (!CanQueue || discovery != null || keyPath == null || queue.Count == 0 || (!scanOnly && target == null)) return;
        var selected = queue.ToArray();
        var batch = new PackageInstallQueue(selected);
        active = new();
        var token = active.Token;
        library.SetSource(null, null, true);
        LockControls();
        bar.Begin();
        string? stopped = null;
        bool finished = false, stopWasError = false;
        int missing = 0;
        try
        {
            await library.StopReading();
            token.ThrowIfCancellationRequested();
            status.Text = "Checking packages and permissions…";
            var plans = await Task.Run(() => {
                var keyset = RememberedKeys.Current.Open(keyPath);
                return batch.Inspect(path => Packages.Inspect(path, keyset, token), token);
            }, token);
            token.ThrowIfCancellationRequested();
            if (scanOnly)
            {
                if (plans.Count > 0)
                {
                    var estimates = await EstimatePackages(plans, keyPath, 0, .99, token);
                    token.ThrowIfCancellationRequested();
                    ++feedbackId;
                    CompressionPreview.ShowResults(this, estimates);
                    status.Text = $"Scan finished · {estimates.Count} game(s)";
                }
                if (batch.Failures.Count > 0)
                {
                    status.Text = $"Scan finished · {batch.Failures.Count} unreadable package(s)";
                    ShowQueueReport("Package scan", string.Join(Environment.NewLine + Environment.NewLine,
                        batch.Failures.Select(f => $"{f.Path}{Environment.NewLine}{f.Reason}")), save: false);
                }
                bar.Report(1);
                return;
            }

            bool compress = compressInstall.Checked;
            var groups = PackageInstallQueue.Groups(plans, compress);
            var details = new List<string>();
            if (compress)
            {
                var usable = new List<InstallGroup>();
                for (int i = 0; i < groups.Count; ++i)
                {
                    var group = groups[i];
                    group.Stage = "Checking compression savings";
                    if (await batch.TryGroup(group, async () => {
                        var estimates = await EstimatePackages(group.Packages, keyPath, .05*i/groups.Count, .05/groups.Count, token);
                        details.AddRange(estimates.Select(e => $"{e.Name} · estimated saving {e.Savings}"));
                    }, token)) usable.Add(group);
                }
                groups = usable;
            }
            else details.AddRange(plans.SelectMany(p => p.Games).Select(g => $"{g.Kind} · {g.Id:X16} · v{g.Version}"));

            ++feedbackId;
            token.ThrowIfCancellationRequested();
            var ready = groups.SelectMany(g => g.Packages).ToList();
            if (ready.Count == 0) { finished = true; return; }
            missing = ready.Sum(p => p.MissingTickets.Length);
            var review = string.Join("\n", details.Take(10));
            if (details.Count > 10) review += $"\n… and {details.Count - 10} more";
            if (compress) review += "\n\nCompression merges the selected update into the base. Keep your original packages. Compressed games require compatible Switch software (12.0.0 or newer) and signature patches. Remove any separate copy or update for this game from the Switch before importing.";
            if (missing > 0) review += $"\n\n{missing} permission(s) have no usable ticket and certificate in these packages. The Switch needs the matching permission to launch them.";
            if (batch.Failures.Count > 0) review += $"\n\n{batch.Failures.Count} package(s) could not be prepared. Details will be shown at the end.";
            if (MessageBox.Show(this, $"Prepare {ready.Count} package(s) on {target!.Root} {target.Label}?\n\n{Requirements}\n\n{review}",
                "Review USB installation", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                stopped = "Cancelled before writing.";
                return;
            }

            await batch.InstallAsync(groups, async (group, index) => {
                var inputs = group.Packages;
                var writes = inputs;
                int id = ++feedbackId;
                string position = $"{index + 1}/{groups.Count}";
                Action? checkLibrary = null;
                using var job = compress ? new CompressionJob() : null;
                double start = (compress ? .05 : 0) + (compress ? .94 : .99) * index / groups.Count;
                double span = (compress ? .94 : .99) / groups.Count;
                if (job != null)
                {
                    group.Stage = "Preparing compressed game";
                    var families = inputs.SelectMany(p => p.Games).Select(g => g.FamilyId).ToHashSet();
                    checkLibrary = () => {
                        var inventory = Library.Scan(target.Root, RememberedKeys.Current.Open(keyPath), token, readControls:false);
                        if (inventory.Unidentified != 0) throw new IOException("Some USB files could not be identified. Resolve them before installing a rebuilt game.");
                        if (inventory.Games.Any(g => families.Contains(g.Id))) throw new IOException("This game already has files on USB. Use USB library → Compress game for its replacement.");
                    };
                    await Task.Run(checkLibrary, token);
                    writes = await job.Convert(inputs, keyPath, Feedback<string>(id, s => status.Text = s), token, CompressionMeter(id, start, .75 * span));
                    start += .75 * span;
                    span *= .25;
                }
                group.Stage = "Installing on USB";
                int writeId = ++feedbackId;
                status.Text = $"{position} · Installing {Path.GetFileName(inputs[0].Path)}";
                var progress = Feedback<Activity>(writeId, a => {
                    bar.Report(start + span * a.Done / Math.Max(1d, a.Total));
                    status.Text = $"{position} · {a.Package} · {a.Stage} · {a.MiBPerSecond:0.0} MiB/s";
                });
                await Task.Run(() => Installer.Write(target.Root, writes, progress, token, target.Check, checkLibrary), token);
            }, target.Check, token);
            finished = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { stopped = scanOnly ? "Scan cancelled." : "Installation cancelled."; }
        catch (Exception error) when (error is InstallDestinationException || PackageInstallQueue.CanReport(error))
        {
            stopWasError = true;
            stopped = (scanOnly ? "Scan stopped. " : "Queue stopped. ") + PackageInstallQueue.Reason(error);
        }
        finally
        {
            ++feedbackId;
            try
            {
                if (!scanOnly)
                {
                    for (int i = queue.Count - 1; i >= 0; i--)
                        if (batch.Installed.Contains(queue[i])) { queue.RemoveAt(i); list.Items.RemoveAt(i); }
                    status.Text = $"{stopped ?? "Queue finished."} Installed: {batch.Installed.Count} · Failed: {batch.Failures.Count}";
                    if (finished) bar.Report(1);
                    if (batch.Failures.Count > 0 || stopWasError)
                    {
                        string report = batch.Report(stopped);
                        if (batch.Installed.Count > 0) report += Environment.NewLine + Environment.NewLine + SwitchImport;
                        ShowQueueReport("Install results", report, save: true);
                    }
                    else if (finished && !closing)
                        MessageBox.Show(this, SwitchImport + (missing > 0 ? "\n\nPackages missing tickets require the corresponding permission on the Switch." : ""),
                            "Ready for Switch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (stopped != null)
                {
                    status.Text = stopped;
                    if (!closing && !token.IsCancellationRequested) MessageBox.Show(this, stopped, "Compression scan");
                }
            }
            finally
            {
                if (!bar.Completed) bar.Clear();
                active.Dispose(); active = null; LockControls();
                if (closing) BeginInvoke(Close);
                else { RefreshDrives(); BindLibrary(true); }
            }
        }
    }

    void ShowQueueReport(string title, string report, bool save)
    {
        string footer = "";
        if (save)
        {
            try
            {
                var folder = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusManagerPC", "Logs"));
                var path = Path.Combine(folder.FullName, $"install-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
                File.WriteAllText(path, report);
                footer = "Saved log: " + path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { footer = "Could not save the log. Select and copy the details below."; }
        }
        if (closing) return;
        using var window = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(750, 410), MinimumSize = new Size(480, 260), ShowInTaskbar = false, MinimizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var text = new TextBox { Text = report, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, AccessibleName = title };
        if (NexusTheme.Enabled) { window.BackColor = NexusTheme.Background; window.ForeColor = NexusTheme.Text; text.BackColor = NexusTheme.Surface; text.ForeColor = NexusTheme.Text; }
        panel.Controls.Add(text, 0, 0);
        panel.Controls.Add(new Label { Text = footer, AutoSize = true, Dock = DockStyle.Fill }, 0, 1);
        var close = ButtonFor("Close", window.Close);
        close.Anchor = AnchorStyles.Right;
        panel.Controls.Add(close, 0, 2);
        window.AcceptButton = window.CancelButton = close;
        window.Controls.Add(panel);
        window.ShowDialog(this);
    }
}
