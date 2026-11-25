using System.Globalization;
using System.Text;

namespace HacBrewPackLz;

/// <summary>
/// What a copy this tool made says about itself, kept as a small file in its control data beside its name and icons, where nothing else looks:
/// which version made it, so a later one knows which copies it can make smaller still, and what the game came to before, so what compressing it
/// freed can be shown for as long as it's there. The Switch app writes the same bytes.
/// </summary>
internal sealed record CopyMark(string MadeBy, long OriginalBytes, ulong TitleId = 0, int Level = 0, int BlockSize = 0)
{
    public bool NexusManagerPc {get;init;}
    public const string FileName = "hacbrewpacklz_copy.txt";
    private const string FirstLine = "hacBrewPackLZ copy 1";

    /// <summary>The first version that stores what a game holds more than once only once, wherever it is. A copy from before it can come out
    /// smaller if compressed again.</summary>
    public const string StoresRepeatsOnce = "1.5.0";

    /// <summary>Whether this copy already stores repeats once, as this version would make it.</summary>
    public bool IsCurrent => CompareVersions(MadeBy, StoresRepeatsOnce) >= 0;

    /// <summary>Whether making this copy again at these settings would save nothing: it's from this version's way of storing, is for this
    /// game, and was made at this level or higher with this block size. A mark that doesn't name its game says nothing.</summary>
    public bool SavesNothingAt(Options options) => IsCurrent && TitleId != 0 && Level >= options.Level && BlockSize == options.BlockSize;

    /// <summary>
    /// What a copy made from this job says: this version, and the game it's made of as it was before this tool made anything of it, which is
    /// what was installed. A copy made again keeps what the first one said that was, and one with nothing to say has it worked out as it
    /// unpacks.
    /// </summary>
    public static CopyMark For(TitleJob job, Options options)
    {
        // For the tests, a copy as an older version would have made it, which then comes up to be made again.
        string madeBy = Environment.GetEnvironmentVariable("HBP_TEST_MADE_BY") is { Length: > 0 } test ? test : AppInfo.Version;
        long original = In(job) is { } before ? before.OriginalBytes
            : Analyzer.IsMadeByThisTool(job) ? Analyzer.UnpackedBytes(job, options) : InstalledBytes(job);
        // For the tests, a mark that names another game, as an edited copy's can.
        ulong title = Environment.GetEnvironmentVariable("HBP_TEST_MARK_FOR") is { Length: > 0 } other ? Convert.ToUInt64(other, 16) : job.Main.Meta.TitleId;
        return new CopyMark(madeBy, original, title, options.Level, options.BlockSize){NexusManagerPc=options.NexusManagerPc};
    }

    /// <summary>What the game takes installed as it is: its contents as the content meta has them, whatever they're packed in, and those of the
    /// update made part of it.</summary>
    public static long InstalledBytes(TitleJob job) =>
        job.Main.Meta.Records.Where(Counts).Sum(r => r.Size) + (job.Update?.Meta.Records.Where(Counts).Sum(r => r.Size) ?? 0);

    private static bool Counts(ContentMetaRecord record) => record.Type is not (Cnmt.ContentTypeMeta or Cnmt.ContentTypeDeltaFragment);

    public byte[] ToBytes() => Encoding.ASCII.GetBytes($"{FirstLine}\nmade by {MadeBy}\noriginal {OriginalBytes.ToString(CultureInfo.InvariantCulture)}\n"
        + (TitleId != 0 ? $"for {TitleId:X16} level {Level.ToString(CultureInfo.InvariantCulture)} block {BlockSize.ToString(CultureInfo.InvariantCulture)}\n" : "") + (NexusManagerPc ? "created by Nexus Manager for PC\n" : ""));

    /// <summary>Nothing, for anything that isn't a mark this made.</summary>
    public static CopyMark? Parse(byte[] data)
    {
        var lines = Encoding.ASCII.GetString(data).Split('\n');
        if (lines.Length < 3 || lines[0] != FirstLine || !lines[1].StartsWith("made by ", StringComparison.Ordinal) || !lines[2].StartsWith("original ", StringComparison.Ordinal))
            return null;
        if (!long.TryParse(lines[2]["original ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long original))
            return null;
        var mark = new CopyMark(lines[1]["made by ".Length..], original);
        // The fourth line is 1.5.2's; one that's missing or doesn't parse leaves the game unknown.
        var fourth = lines.Length > 3 ? lines[3].Split(' ') : [];
        return fourth.Length == 6 && fourth[0] == "for" && fourth[2] == "level" && fourth[4] == "block" && fourth[1].Length == 16
            && ulong.TryParse(fourth[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong title)
            && int.TryParse(fourth[3], NumberStyles.None, CultureInfo.InvariantCulture, out int level)
            && int.TryParse(fourth[5], NumberStyles.None, CultureInfo.InvariantCulture, out int block)
            ? mark with { TitleId = title, Level = level, BlockSize = block } : mark;
    }

    /// <summary>The mark in a game's control data, if it has one for this game.</summary>
    public static CopyMark? In(TitleJob job) =>
        job.Contents.Where(c => c.Type == Cnmt.ContentTypeControl).Select(c => InControl(c.Primary, job.Main.Meta.TitleId))
            .FirstOrDefault(m => m is not null);

    /// <summary>The mark in a control NCA, if it has one for this game, as the Switch reads it (parseFor). A mark that doesn't name its game
    /// counts for any.</summary>
    public static CopyMark? InControl(NcaFile nca, ulong titleId) => InControl(nca) is { } mark && (mark.TitleId == 0 || mark.TitleId == titleId) ? mark : null;

    /// <summary>The mark in a control NCA, if it has one, whichever game it names.</summary>
    public static CopyMark? InControl(NcaFile nca)
    {
        foreach (var section in nca.Sections)
        {
            if (section is null || section.FsType != NcaFsSection.FsRomFs || section.HasIndirect)
                continue;
            var image = nca.OpenRomFsImage(section, null, verify: false);
            var file = RomFsArchive.Open(image, $"{nca.Name} section {section.Index}").Files.FirstOrDefault(f => f.Path == FileName);
            if (file is null || file.Size > 4096)
                continue;
            var data = new byte[file.Size];
            image.Read(file.Offset, data, 0, data.Length);
            return Parse(data);
        }
        return null;
    }

    /// <summary>Versions by their numbers, 1.10 after 1.9, and a test version as the one it leads to.</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] Parts(string v) => v.Split('-')[0].Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();
        int[] x = Parts(a), y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0)
                return c;
        }
        return 0;
    }
}
