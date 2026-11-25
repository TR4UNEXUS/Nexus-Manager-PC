using System.Security.Cryptography;
using System.Text;

namespace HacBrewPackLz;

// The desktop app supplies a private working directory and the selected input packages.
// Reading keys from the inherited pipe keeps them out of temporary files and arguments.
internal static class NexusCompressionHost
{
    static int Main(string[] args)
    {
        Keyset? keys = null;
        try
        {
            if (args.Length < 2 || !Console.IsInputRedirected)
                throw new IOException("Start compression from Nexus Manager for PC.");
            var output = Path.GetFullPath(args[0]);
            if (!Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any())
                throw new IOException("Compression needs an empty output folder.");
            bool installed=args[1]=="--scan-installed";
            bool scan=installed||args[1]=="--scan-packages";
            using var reader = new BinaryReader(Console.OpenStandardInput());
            int length = reader.ReadInt32();
            if (length is < 1 or > 4194304) throw new IOException("Invalid local key file.");
            var bytes = reader.ReadBytes(length);
            try
            {
                if (bytes.Length != length) throw new EndOfStreamException();
                using var source = new StreamReader(new MemoryStream(bytes, false), Encoding.UTF8);
                keys = Keyset.FromReader(source);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            // Keep abandoned-session cleanup from racing a compressor whose parent quit.
            var leasePath = Environment.GetEnvironmentVariable("NEXUS_COMPRESSION_LEASE");
            using var lease = leasePath == null ? null : File.Open(leasePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var options = new Options {
                Inputs = args.Skip(scan?2:1).Select(Path.GetFullPath).ToList(),
                TempDir = Path.Combine(output, "work"), NspDir = output, NspDirSet = true,
                Compress = true, NexusManagerPc = true, VerifyAfterBuild = true, Threads = Math.Clamp(Environment.ProcessorCount, 1, 8)
            };
            Directory.CreateDirectory(options.TempDir);
            var sink=new DesktopSink();
            Ui.Sink=sink;
            options.PlanCompressionProgress=jobs=>sink.Plan(jobs,options);
            options.BeginCompressionSection=sink.BeginSection;
            if(!scan)return Batch.Run(options, keys, BatchMode.Convert);
            using var set=installed?TitleSet.LoadInstalled(ReadInstalled(reader),keys):TitleSet.Load(options.Inputs,keys);
            if(set.Problems.Count>0)throw new IOException(set.Problems[0].Message);
            if(set.Jobs.Count==0)throw new IOException("No games could be analysed.");
            var results=new List<object>();
            int scanned=0;
            foreach(var group in set.Jobs.GroupBy(job=>job.ApplicationId)){
                var game=group.Single(j=>j.IsBaseGame);
                Ui.Info("Scanning "+game.DisplayName);
                var parts=new List<SizeEstimate>();
                foreach(var job in group){
                    sink.Report("Scanning "+game.DisplayName,scanned==0?null:(double)scanned/set.Jobs.Count);
                    parts.Add(Analyzer.Estimate(job,options,Analyzer.KnownAsItIs(job,set.Jobs,set,options)));
                    sink.Report("Scanning "+game.DisplayName,(double)++scanned/set.Jobs.Count);
                }
                var estimate=SizeEstimate.Together(parts).AsInstalled();
                // Convert leaves a developer-compressed base alone. It cannot promise a smaller installation.
                bool skipped=Analyzer.ByMakers(game);
                long after=skipped||estimate.AlreadyCompressed?estimate.InputBytes:estimate.MostBytes;
                results.Add(new {GameId=group.Key,Name=game.DisplayName,BeforeBytes=estimate.InputBytes,
                    AfterBytes=after,Skipped=skipped,AlreadyAtSize=estimate.AlreadyCompressed});
            }
            File.WriteAllText(Path.Combine(output,"estimates.json"),System.Text.Json.JsonSerializer.Serialize(results));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Help.Clean(ex.Message, keys));
            return 1;
        }
    }

    static GameArchive ReadInstalled(BinaryReader reader){
        int count=reader.ReadInt32();
        if(count is <1 or >8192)throw new InvalidDataException("Invalid number of game files.");
        var entries=new List<(string,DataSource)>(count);
        for(int i=0;i<count;i++){
            string name=reader.ReadString();long size=reader.ReadInt64();string path=reader.ReadString();
            if(size<=0||name.Length>80)throw new InvalidDataException("Invalid game file information.");
            if(path.Length>0){
                entries.Add((name,new FileSource(path,size)));
            }else{
                if(size>65536)throw new InvalidDataException("Invalid permission file size.");
                var bytes=reader.ReadBytes((int)size);
                if(bytes.Length!=size)throw new EndOfStreamException();
                entries.Add((name,new BytesSource(bytes)));
            }
        }
        return new GameArchive("installed.nsp",entries);
    }
}

internal sealed class DesktopSink : IUiSink
{
    long totalWeight,completedWeight,sectionWeight;
    double? lastFraction;
    string? lastStage;
    public TimeSpan ProgressInterval=>TimeSpan.FromMilliseconds(200);
    public void Plan(IReadOnlyList<TitleJob> jobs,Options options){
        foreach(var content in jobs.SelectMany(job=>job.Contents))
            foreach(int i in content.SectionsInFileOrder()){
                var source=content.SectionAt(i)!.Value;
                if(TitleConverter.Plan(content,source,options)==SectionPlan.CompressRomFs)
                    totalWeight=checked(totalWeight+Math.Max(1,source.Section.DataLevelSize));
            }
    }
    public void BeginSection(long weight)=>sectionWeight=weight;
    public void Message(UiLevel level,string text){
        if(level==UiLevel.Error)Console.Error.WriteLine(text);
        else if(level==UiLevel.Warning)Console.WriteLine("Warning: "+text.ReplaceLineEndings(" "));
    }
    public void Progress(ProgressSnapshot snapshot){
        var fraction=lastFraction;
        if(snapshot.Label=="Compressing RomFS"&&totalWeight>0&&snapshot.Total>0){
            double part=Math.Clamp((double)snapshot.Done/snapshot.Total,0,1);
            fraction=(completedWeight+sectionWeight*part)/totalWeight;
        }
        Report(ProgressReporter.Plain(snapshot.Label),fraction);
    }
    public void ProgressDone(string label,TimeSpan elapsed){
        if(label=="Compressing RomFS"&&sectionWeight>0){
            completedWeight+=sectionWeight;sectionWeight=0;
            if(totalWeight>0)Report("Compressing the game data",(double)completedWeight/totalWeight);
        }
    }
    public void Report(string stage,double? fraction){
        if(fraction.HasValue)fraction=Math.Clamp(fraction.Value,lastFraction??0,1);
        else fraction=lastFraction;
        if(stage==lastStage&&fraction==lastFraction)return;
        lastStage=stage;lastFraction=fraction;
        Console.WriteLine("NEXUS-PROGRESS "+System.Text.Json.JsonSerializer.Serialize(new {Stage=stage,Fraction=fraction}));
    }
}
