using System.Diagnostics;
using System.Security.Cryptography;
using LibHac.Common.Keys;
using Nexus.Pc;

namespace Nexus.Desktop;

internal sealed class CompressionJob : IDisposable
{
    readonly string folder;
    readonly FileStream lease;
    internal string Source => Path.Combine(folder,"installed.nsp");

    static string ScratchRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NexusManagerPC","compression");
    internal static void RecoverAbandoned()
    {
        var parent=ScratchRoot;
        if(!Directory.Exists(parent))return;
        foreach (var old in Directory.EnumerateDirectories(parent)) {
            if (!Guid.TryParseExact(Path.GetFileName(old),"N",out _)) continue;
            try {
                if(File.GetAttributes(old).HasFlag(FileAttributes.ReparsePoint))continue;
                // Another instance holds this handle for the full operation.
                using (File.Open(Path.Combine(old,"session"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { }
                Directory.Delete(old,true);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public CompressionJob(){
        RecoverAbandoned();
        var parent=Directory.CreateDirectory(ScratchRoot).FullName;
        folder = Directory.CreateDirectory(Path.Combine(parent,Guid.NewGuid().ToString("N"))).FullName;
        lease = File.Open(Path.Combine(folder,"session"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
    }
    public void Dispose()
    {
        lease.Dispose();
        try { Directory.Delete(folder,true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal async Task<List<Package>> Convert(IReadOnlyList<Package> inputs, string keyPath, IProgress<string> progress, CancellationToken token,IProgress<CompressionUpdate>? meter=null)
    {
        var locks=new List<FileStream>();
        try {
            foreach(var path in inputs.Select(p=>p.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                locks.Add(File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read));
            return await ConvertLocked(inputs,keyPath,progress,token,meter).ConfigureAwait(false);
        } finally {foreach(var file in locks)file.Dispose();}
    }

    async Task<List<Package>> ConvertLocked(IReadOnlyList<Package> inputs, string keyPath, IProgress<string> progress, CancellationToken token,IProgress<CompressionUpdate>? meter=null)
    {
        Compression.ExpectedTitles(inputs.SelectMany(p=>p.Games));
        if(inputs.Any(p=>p.MissingTickets.Length>0))
            throw new InvalidDataException("Compression needs the original ticket and certificate for protected content. Use the original package or choose normal install.");
        progress.Report("Checking original game files…");
        await Verify(inputs,"Checking original game files",0,.12).ConfigureAwait(false);
        string output=await RunHost(inputs.Select(p=>p.Path),keyPath,progress,token,report:tick=>meter?.Report(tick with {Fraction=.12+.74*(tick.Fraction??0)})).ConfigureAwait(false);
        var keys=RememberedKeys.Current.Open(keyPath);
        var converted=await Task.Run(()=>Directory.GetFiles(output,"*.nsp").Select(p=>Packages.Inspect(p,keys,token)).ToList(),token).ConfigureAwait(false);
        if(converted.Count==0) throw new InvalidDataException("No smaller game was produced. Use normal install for this game.");
        Compression.VerifyOutput(inputs,converted);
        await Verify(converted,"Verifying compressed files",.86,.14).ConfigureAwait(false);
        // Retain original permission backups alongside a newly installed library.
        var tickets=inputs.SelectMany(p=>p.Tickets).GroupBy(t=>t.Id).Select(g=>{
            var first=g.First();
            if(g.Any(x=>!x.Bundle.SequenceEqual(first.Bundle)))throw new InvalidDataException("Conflicting original tickets in this selection.");
            return first;
        }).ToList();
        converted[0]=converted[0] with {Tickets=tickets};
        return converted;

        Task Verify(IReadOnlyList<Package> packages,string stage,double start,double range)=>Task.Run(()=>{
            long total=packages.Sum(p=>p.Files.Sum(c=>c.Part.Size)),done=0,last=-200;
            var clock=Stopwatch.StartNew();meter?.Report(new(stage,start));
            foreach(var package in packages)Compression.VerifyInput(package,token,count=>{
                done+=count;
                if(total>0&&(done==total||clock.ElapsedMilliseconds-last>=200)){
                    last=clock.ElapsedMilliseconds;meter?.Report(new(stage,start+range*done/total));
                }
            });
        },token);
    }

    internal async Task<List<CompressionEstimate>> Estimate(IEnumerable<string> paths,string keyPath,IProgress<string> progress,
        CancellationToken token,IReadOnlyList<Compression.Entry>? entries=null,IProgress<CompressionUpdate>? meter=null){
        string output=await RunHost(paths,keyPath,progress,token,entries==null?"--scan-packages":"--scan-installed",entries,tick=>meter?.Report(tick)).ConfigureAwait(false);
        string report=Path.Combine(output,"estimates.json");
        if(!File.Exists(report)||new FileInfo(report).Length>4*1024*1024)throw new IOException("The scan could not return a result.");
        var results=System.Text.Json.JsonSerializer.Deserialize<List<CompressionEstimate>>(await File.ReadAllBytesAsync(report,token).ConfigureAwait(false));
        if(results==null||results.Count==0||results.Any(r=>r.BeforeBytes<=0||r.AfterBytes<0)||results.Select(r=>r.GameId).Distinct().Count()!=results.Count)
            throw new InvalidDataException("The scan returned incomplete size information.");
        return results;
    }

    async Task<string> RunHost(IEnumerable<string> paths,string keyPath,IProgress<string> progress,CancellationToken token,
        string? mode=null,IReadOnlyList<Compression.Entry>? entries=null,Action<CompressionUpdate>? report=null){
        token.ThrowIfCancellationRequested();
        string engine = Path.Combine(AppContext.BaseDirectory,"Compression","NexusCompressionHost.exe");
        if (!File.Exists(engine)) throw new IOException("Extract the complete Windows download, including its Compression folder.");
        string output = Directory.CreateDirectory(Path.Combine(folder,mode??"output")).FullName;
        var start = new ProcessStartInfo(engine) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            WorkingDirectory=folder
        };
        start.Environment["NEXUS_COMPRESSION_LEASE"]=Path.Combine(folder,"session");
        start.Environment["TMP"]=folder;start.Environment["TEMP"]=folder;
        start.ArgumentList.Add(output);
        if(mode!=null)start.ArgumentList.Add(mode);
        foreach (var path in paths) start.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(start) ?? throw new IOException("Could not start the local compressor.");
        void Stop() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        using var cancel = token.Register(Stop);
        string? error=null, lastWarning=null;
        async Task Drain(StreamReader reader,bool errors) {
            while(await reader.ReadLineAsync().ConfigureAwait(false) is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if(!errors&&line.StartsWith("NEXUS-PROGRESS ",StringComparison.Ordinal)){
                    var tick=System.Text.Json.JsonSerializer.Deserialize<CompressionUpdate>(line.Substring(15));
                    if(tick!=null && !string.IsNullOrEmpty(tick.Stage)){
                        if(report!=null)report(tick);else progress.Report(tick.Stage);
                    }
                    continue;
                }
                if(errors) error=line;
                else {
                    if(line.StartsWith("Warning:",StringComparison.OrdinalIgnoreCase) || line.StartsWith("Left out:",StringComparison.OrdinalIgnoreCase)) lastWarning=line;
                    progress.Report(line);
                }
            }
        }
        var stdout=Drain(process.StandardOutput,false);var stderr=Drain(process.StandardError,true);
        try {
            var raw=RememberedKeys.Current.ReadForCompression(keyPath);
            try {
                using var pipe=new BinaryWriter(process.StandardInput.BaseStream);
                pipe.Write(raw.Length);pipe.Write(raw);
                if(entries!=null){
                    pipe.Write(entries.Count);
                    foreach(var entry in entries){
                        token.ThrowIfCancellationRequested();
                        pipe.Write(entry.Name);pipe.Write(entry.Length);pipe.Write(entry.Path??"");
                        if(entry.Path==null)pipe.Write(entry.Data!);
                    }
                }
            } finally {CryptographicOperations.ZeroMemory(raw);}
            await process.WaitForExitAsync().ConfigureAwait(false);
        } finally {
            Stop();await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout,stderr).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        if(process.ExitCode!=0) throw new InvalidDataException(error??lastWarning??"The compressor could not read the selected game and update.");
        if(mode==null&&!Directory.EnumerateFiles(output,"*.nsp").Any())
            throw new InvalidDataException(lastWarning??"No smaller game was produced.");
        return output;
    }
}

internal sealed record CompressionEstimate(ulong GameId,string Name,long BeforeBytes,long AfterBytes,bool Skipped,bool AlreadyAtSize)
{
    public bool BaseOnly {get;init;}
    public string RowSavings=>BaseOnly?(Skipped?"Base only · skipped":$"Base only · {Percent:0.#}%"):Savings;
    public long SavedBytes=>Math.Max(0,BeforeBytes-AfterBytes);
    public double Percent=>100.0*SavedBytes/BeforeBytes;
    public static string Size(long bytes)=>bytes>=1L<<30?$"{bytes/(double)(1L<<30):0.##} GiB":
        bytes>=1L<<20?$"{bytes/(double)(1L<<20):0.##} MiB":bytes>=1024?$"{bytes/1024d:0.#} KiB":$"{bytes} B";
    public string Savings=>Skipped?"Skipped by compressor":AlreadyAtSize?"0% · already at size":
        SavedBytes==0?"0% · no saving predicted":$"{Percent:0.#}% · {Size(SavedBytes)}";
    public string Description=>(BaseOnly?"Base game only. A matching permission backup for the update or DLC is unavailable on this PC.\n":"")+$"Current {Size(BeforeBytes)}   →   Estimated {Size(AfterBytes)}\nEstimated saving   {Savings}";
}

internal sealed record CompressionUpdate(string Stage,double? Fraction);
