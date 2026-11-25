using System.Text;

namespace HacBrewPackLz;

/// <summary>Which jobs read an option.</summary>
[Flags]
internal enum OptionUse
{
    Build = 1,
    Convert = 2,
    Extract = 4,
}

internal enum OptionKind
{
    Toggle,
    Slider,
    Choice,
    Number,
    Text,
    Hex,
    File,
    Folder,

    /// <summary>Not a setting but something to do, with a button, among the options of the home screen and the GUI.</summary>
    Action,
}

internal sealed record OptionSpec(string Flag, string Label, OptionKind Kind, OptionUse Use, string Help)
{
    /// <summary>Whether a toggle's flag is on, with nothing stored meaning its default.</summary>
    public bool ToggleOn(string stored) => (stored.Length > 0 ? stored : DefaultApplies ? Default : "") == "1";

    /// <summary>What to store for a toggle's flag: nothing for its default, so a toggle that's on to begin with is kept off with a 0.</summary>
    public string ToggleValue(bool on) => on == ToggleOn("") ? "" : on ? "1" : "0";

    /// <summary>Also accepted on the command line.</summary>
    public string? Short { get; init; }

    /// <summary>What leaving it empty means, for people reading the console screen or the GUI.</summary>
    public string Placeholder { get; init; } = "";

    public string[] Choices { get; init; } = [];
    public int Min { get; init; }
    public int Max { get; init; }

    /// <summary>Starting value for a slider or a choice.</summary>
    public string Default { get; init; } = "";

    /// <summary>The home screen and the GUI go by the default as if it had been chosen, even though the command line doesn't.</summary>
    public bool DefaultApplies { get; init; }

    /// <summary>The toggle reads "on" when the flag is absent.</summary>
    public bool Inverted { get; init; }

    /// <summary>Help for the command line when it has to say more than the GUI does.</summary>
    public string? CommandHelp { get; init; }

    /// <summary>What an action's button says.</summary>
    public string Verb { get; init; } = "";

    /// <summary>What it does in a few words, on one line, where each option has to take the same room, as on the welcome.</summary>
    public string? Brief { get; init; }
}

internal sealed record OptionGroup(string Title, OptionSpec[] Options);

/// <summary>Every option, in one place: the command line help, the console screen and the GUI are all made from it.</summary>
internal static class OptionCatalog
{
    private const OptionUse Rebuilds = OptionUse.Build | OptionUse.Convert;
    private const OptionUse All = OptionUse.Build | OptionUse.Convert | OptionUse.Extract;

    public static readonly OptionGroup Compression = new("Compression",
    [
        new("--nocompress", "Compress game data", OptionKind.Toggle, Rebuilds,
            "Off, games are repacked just as they are, with an update on the list made part of its game. That's quicker, and a game only comes out smaller if it had an update.")
        {
            Inverted = true, CommandHelp = "Store the RomFS uncompressed, like hacBrewPack",
        },
        new("--level", "Compression level", OptionKind.Slider, Rebuilds,
            "1 and 2 are LZ4, the fastest. 3 to 12 are LZ4 HC, smaller and slower to pack. 9 is a good balance.")
        {
            Min = Lz4Encoder.MinLevel, Max = Lz4Encoder.MaxLevel, Default = "9",
            CommandHelp = "1-2 LZ4 (fastest), 3-12 LZ4 HC (smaller, slower to pack), default 9",
        },
        new("--blocksize", "Block size", OptionKind.Choice, Rebuilds,
            "Larger blocks compress better. 64K is what retail games use.")
        {
            Choices = ["4K", "8K", "16K", "32K", "64K"], Default = "64K",
            CommandHelp = "Compression block size, power of two from 4K to 64K, default 64K",
        },
        new("--threads", "Threads", OptionKind.Number, Rebuilds,
            "How many processor threads to compress with.")
        {
            Min = 1, Max = 1024, Placeholder = "All of them",
            CommandHelp = "Number of compression threads, default all logical processors",
        },
    ]);

    public static readonly OptionGroup Worthwhile = new("Which games are worth it",
    [
        new("--minsaving", "Minimum saving", OptionKind.Choice, OptionUse.Convert,
            "Games measured to save less than this go under Done, so the time goes where it gains the most. You can still tick them yourself.")
        {
            Brief = "Leaves out games that save less",
            Choices = ["Off", "5%", "10%", "15%", "20%", "25%", "30%", "40%", "50%"], Default = "5%", DefaultApplies = true,
            CommandHelp = "Skip titles that would save less than this percentage. Each one is analyzed first. Default off",
        },
        new("--measure", "Thorough analysis", OptionKind.Toggle, OptionUse.Convert,
            "On, analyzing goes on after its quick first look at each game to read every game in full, writing nothing, the ones it could change the answer for first, so every figure is exact and the ones worth it are ticked. The list can be used meanwhile, and compressing or anything else stops it. Off, games a first look can't vouch for stay unticked.")
        {
            Default = "1", DefaultApplies = true,
            Brief = "Reads every game in full for exact figures",
            CommandHelp = "Read in full, writing nothing, the titles a first look can't vouch for, so analyze says what they save and convert compresses those worth it. Otherwise convert leaves them out",
        },
        new("--hidesmall", "Hide games under 1 MB", OptionKind.Toggle, OptionUse.Convert | OptionUse.Extract,
            "Leaves out games that small with no update or DLC, like shortcuts to homebrew apps on the HOME Menu, which aren't worth listing.")
        {
            Default = "1", DefaultApplies = true,
            CommandHelp = "Leave out games under 1 MB with no update or DLC, like shortcuts to homebrew apps (forwarders). On to begin with on the home screen and in the GUI",
        },
        new("--showunusable", "Hide files that can't be used", OptionKind.Toggle, 0,
            "Files found in folders that aren't games, or can't be read, stay out of To do. Everything still lists them, at the top. A file you add by itself always says what's wrong with it.")
        {
            Inverted = true, CommandHelp = "On the home screen and in the GUI, list the files found in folders that can't be used",
        },
    ]);

    public static readonly OptionGroup Output = new("Output",
    [
        new("--nspdir", "NSP folder", OptionKind.Folder, Rebuilds,
            "Where finished NSPs go.")
        {
            Brief = "Where compressed games are saved",
            Placeholder = "Beside the source file",
            CommandHelp = "Set output nsp directory. Default when converting is beside the input, and when building .\\hacbrewpack_nsp\\",
        },
        new("--outdir", "Extract folder", OptionKind.Folder, OptionUse.Extract,
            "Where extracted files go, in a folder for each title.")
        {
            Placeholder = "Beside the source file",
            CommandHelp = "Set output directory when extracting, default is beside the input",
        },
        new("--tempdir", "Temporary folder", OptionKind.Folder, Rebuilds,
            "Only used for a solid NSZ that has to be gone back to. Nothing else needs one.")
        {
            Placeholder = "The NSP folder",
            CommandHelp = "Set directory for what's read of a solid NSZ that has to be gone back to, default is the output nsp directory",
        },
        new("--verify", "Check each finished NSP", OptionKind.Toggle, Rebuilds,
            "Reads the new file back and checks all of it. Takes a while longer.")
        {
            Brief = "Reads each one back to check it",
        },
        new("--separatedlc", "Game and DLC in one file", OptionKind.Toggle, OptionUse.Convert,
            "On, a game's new file holds its DLC too, so emulators find it without installing it, and DLC without their game go together in one file. Off, each DLC gets a file of its own.")
        {
            Inverted = true, CommandHelp = "Save each DLC as its own file, not in the game's file",
        },
        new("--keepncadir", "Also write the NCAs", OptionKind.Toggle, Rebuilds,
            "Copies the finished NCAs to the NCA folder as well.")
        {
            CommandHelp = "Also write the NCAs to the nca directory",
        },
        new("--ncadir", "NCA folder", OptionKind.Folder, Rebuilds,
            "Where those NCAs go.")
        {
            Placeholder = "hacbrewpack_nca",
            CommandHelp = "Set output nca directory path used by --keepncadir, default path is .\\hacbrewpack_nca\\",
        },
    ]);

    private const string NamePartHelp = "Whether new and renamed files' names have it.";

    /// <summary>Which parts a game's new file name has. The screens show what a name is like under each, as it's ticked (Settings.HelpOf).</summary>
    public static readonly OptionGroup Names = new("File names",
    [
        new("--noidinname", "Title ID", OptionKind.Toggle, Rebuilds, NamePartHelp) { Inverted = true, CommandHelp = "Leave the title ID out of new files' names" },
        new("--noversioninname", "Version", OptionKind.Toggle, Rebuilds, NamePartHelp) { Inverted = true, CommandHelp = "Leave the version out of new files' names" },
        new("--nodlccountinname", "How many DLC", OptionKind.Toggle, Rebuilds, NamePartHelp) { Inverted = true, CommandHelp = "Leave how many DLC out of new files' names" },
        new("--nolz4inname", "[LZ4]", OptionKind.Toggle, Rebuilds, NamePartHelp) { Inverted = true, CommandHelp = "Leave [LZ4] out of new files' names" },
    ]);

    public static readonly OptionGroup Keys = new("Keys and signing",
    [
        new("--keyset", "Keys file", OptionKind.File, All,
            "Your console keys. prod.keys beside this program is found by itself.")
        {
            Short = "-k", Placeholder = "prod.keys beside this program",
            CommandHelp = "Set keyset filepath, default looks for keys.dat, keys.txt, keys.ini and prod.keys",
        },
        new("--keygeneration", "Key generation", OptionKind.Number, Rebuilds,
            "Which key area key encrypts the new NCAs.")
        {
            Min = 1, Max = 32, Placeholder = "1",
            CommandHelp = "Set keygeneration for encrypting key area, default keygeneration is 1",
        },
        new("--keyareakey", "Key area key 2", OptionKind.Hex, Rebuilds,
            "32 hex digits.")
        {
            Placeholder = "04040404040404040404040404040404",
            CommandHelp = "Set key area key 2 in hex with 16 bytes length",
        },
        new("--sdkversion", "SDK version", OptionKind.Hex, Rebuilds,
            "In hex. When converting, the default keeps each source's own version.")
        {
            Placeholder = "000C1100",
            CommandHelp = "Set SDK version in hex, default SDK version is 000C1100",
        },
        new("--plaintext", "Don't encrypt sections", OptionKind.Toggle, Rebuilds,
            "Writes the sections unencrypted and marks them as plaintext.")
        {
            CommandHelp = "Skip encrypting sections and set section header block crypto type to plaintext",
        },
        new("--nosignncasig2", "Don't sign NCAs", OptionKind.Toggle, Rebuilds,
            "Keeps the original ACID public key and skips the second NCA signature.")
        {
            CommandHelp = "Skip patching acid public key in npdm and signing nca header with acid public key",
        },
    ]);

    public static readonly OptionGroup Versions = new("Versions",
    [
        new("--titleversion", "Title version", OptionKind.Hex, Rebuilds,
            "In hex. The default is 0, or the source's version when converting.")
        {
            Placeholder = "From the source",
            CommandHelp = "Set title version in the content meta in hex, default 0, or the source's version when converting",
        },
        new("--requiredsystemversion", "Required system version", OptionKind.Hex, Rebuilds,
            "In hex. The default is 12.0.0 when compressing, or the source's if that is higher.")
        {
            Placeholder = "Worked out for you",
            CommandHelp = "Set required system version in the content meta in hex, default 30000000 (12.0.0) when compressing, otherwise 0, or the source's if higher when converting",
        },
    ]);

    public static readonly OptionGroup BuildFolders = new("Folders",
    [
        new("--exefsdir", "ExeFS folder", OptionKind.Folder, OptionUse.Build, "Must hold main.npdm.")
        {
            Placeholder = ".\\exefs",
            CommandHelp = "Set program exefs directory path, default path is .\\exefs\\",
        },
        new("--romfsdir", "RomFS folder", OptionKind.Folder, OptionUse.Build, "The game's data.")
        {
            Placeholder = ".\\romfs",
            CommandHelp = "Set program romfs directory path, default path is .\\romfs\\",
        },
        new("--logodir", "Logo folder", OptionKind.Folder, OptionUse.Build, "The boot logo files.")
        {
            Placeholder = ".\\logo",
            CommandHelp = "Set program logo directory path, default path is .\\logo\\",
        },
        new("--controldir", "Control folder", OptionKind.Folder, OptionUse.Build, "Must hold control.nacp.")
        {
            Placeholder = ".\\control",
            CommandHelp = "Set control romfs directory path, default path is .\\control\\",
        },
        new("--htmldocdir", "HtmlDocument folder", OptionKind.Folder, OptionUse.Build, "Optional in-game manual.")
        {
            Placeholder = "None",
            CommandHelp = "Set HtmlDocument romfs directory path",
        },
        new("--legalinfodir", "LegalInformation folder", OptionKind.Folder, OptionUse.Build, "Optional legal notices.")
        {
            Placeholder = "None",
            CommandHelp = "Set LegalInformation romfs directory path",
        },
        new("--output", "Output file", OptionKind.File, OptionUse.Build, "The NSP to write. The default is named after the title ID, in the NSP folder.")
        {
            Short = "-o", Placeholder = "<title id>.nsp in the NSP folder",
            CommandHelp = "Set output nsp filepath, for a single title. Overrides --nspdir",
        },
        new("--noromfs", "No RomFS", OptionKind.Toggle, OptionUse.Build, "Builds the Program NCA without a RomFS section.")
        {
            CommandHelp = "Skip creating program romfs section",
        },
        new("--nologo", "No logo", OptionKind.Toggle, OptionUse.Build, "Builds the Program NCA without a logo section.")
        {
            CommandHelp = "Skip creating program logo section",
        },
    ]);

    public static readonly OptionGroup BuildTitle = new("Title",
    [
        new("--titleid", "Title ID", OptionKind.Hex, OptionUse.Build, "16 hex digits. Replaces the ID in the NCAs, the NPDM and the NACP.")
        {
            Placeholder = "From main.npdm",
            CommandHelp = "Use specified titleid for creating ncas and patch titleid in npdm and nacp",
        },
        new("--titlename", "Title name", OptionKind.Text, OptionUse.Build, "Replaces the name in the NACP for all languages, up to 512 bytes.")
        {
            Placeholder = "From control.nacp",
            CommandHelp = "Change title name in nacp for all languages, max size is 512 bytes",
        },
        new("--titlepublisher", "Publisher", OptionKind.Text, OptionUse.Build, "Replaces the publisher in the NACP for all languages, up to 256 bytes.")
        {
            Placeholder = "From control.nacp",
            CommandHelp = "Change title publisher in nacp for all languages, max size is 256 bytes",
        },
        new("--nopatchnacplogo", "Keep the NACP logo setting", OptionKind.Toggle, OptionUse.Build, "Normally logo handling is changed to automatic.")
        {
            CommandHelp = "Skip changing logo handling to auto in NACP",
        },
    ]);

    public static readonly OptionGroup Updates = new("Updates",
    [
        new("--noautoupdate", "Keep this app up to date", OptionKind.Toggle, 0,
            "Each time it opens, it looks for a newer version and installs it. Whatever it gets has to carry the right signature.")
        {
            Inverted = true, CommandHelp = "On the home screen and in the GUI, don't look for a newer version when opening",
            Brief = "Updates this app when it opens",
        },
        new(ProblemReports.Flag, "Send problem reports", OptionKind.Toggle, 0,
            "When something goes wrong, a report goes to the developer by itself, so it can be fixed. Your keys are never in it.")
        {
            CommandHelp = "When a game can't be done, send a report of it to the developer, with no keys in it, once a version for each problem",
            Brief = "Sends a report when something goes wrong",
        },
    ]);

    public static readonly OptionGroup[] Groups = [Compression, Worthwhile, Output, Names, Keys, Versions, BuildFolders, BuildTitle, Updates];

    /// <summary>Looks for a newer version there and then. The command line has the update command instead.</summary>
    public static readonly OptionSpec CheckForUpdates = new("check-for-updates", "Check for a new version", OptionKind.Action, 0,
        "Looks for a newer version now, and installs it if there is one.")
    {
        Verb = "Check now",
    };

    /// <summary>What came up the first time it opened, again, as on the Switch.</summary>
    public static readonly OptionSpec ShowWelcome = new("show-welcome", "Welcome screen", OptionKind.Action, 0,
        "Shows what came up the first time this was opened, with the most important settings all in one place.")
    {
        Verb = "Show it",
    };

    /// <summary>
    /// What the command line still takes but someone compressing their games has no use for: building NSPs by hand, as with hacBrewPack, some
    /// of which makes NSPs that won't install or start; a block size other than the 64K retail games use, which only compresses worse; and
    /// a temporary folder only a solid NSZ ever needs, which the NSP folder already does for. The home screen, the GUI and Android neither
    /// show it nor go by it, even if an earlier version saved it.
    /// </summary>
    public static readonly HashSet<string> CommandLineOnly =
    [
        "--keepncadir", "--ncadir", "--keygeneration", "--keyareakey", "--sdkversion", "--plaintext", "--nosignncasig2",
        "--titleversion", "--requiredsystemversion", "--blocksize", "--tempdir",
    ];

    /// <summary>What only means anything while compressing: with compression off nothing is compressed or measured, nor saves anything to go
    /// by. These go while it's off, on every version, as on the Switch.</summary>
    public static readonly HashSet<string> CompressionOnly = ["--level", "--minsaving", "--measure"];

    /// <summary>Whether an option is shown, by what's set: one that only means anything while compressing isn't while compression is off.</summary>
    public static bool Shown(OptionSpec option, Func<string, string> get) => !(CompressionOnly.Contains(option.Flag) && get("--nocompress") == "1");

    /// <summary>Whether changing this option can change which others are shown.</summary>
    public static bool DecidesShown(OptionSpec option) => option.Flag == "--nocompress";

    private static OptionGroup WithoutCommandLineOnly(OptionGroup group) =>
        group with { Options = group.Options.Where(o => !CommandLineOnly.Contains(o.Flag)).ToArray() };

    /// <summary>The groups a list of titles uses, with checking for updates and the welcome beside the setting that updates by itself.</summary>
    public static readonly OptionGroup[] GeneralGroups =
    [
        WithoutCommandLineOnly(Compression), Worthwhile, WithoutCommandLineOnly(Output), Names, WithoutCommandLineOnly(Keys) with { Title = "Keys" },
        Updates with { Title = "This app", Options = [.. Updates.Options, CheckForUpdates, ShowWelcome] },
    ];

    public static IEnumerable<OptionSpec> AllOptions => Groups.SelectMany(g => g.Options);

    private static OptionSpec Find(string flag) => AllOptions.First(o => o.Flag == flag);

    /// <summary>
    /// What most people will want a say in, all in one place, the first time it opens, as on the Switch. Everything else, and all
    /// of this again, is under Options.
    /// </summary>
    public static readonly OptionGroup Welcome = new("Welcome to hacBrewPackLZ",
        [Find("--nspdir"), Find("--verify"), Find("--minsaving"), Find("--noautoupdate"), Find(ProblemReports.Flag)]);

    /// <summary>What it is, in a sentence. The welcome says this, then what most people don't know, then what to watch out for.</summary>
    public const string WelcomeIntro =
        "Stores games compressed, in a form the Switch itself reads: they take less room and play the same. Your own files are never changed.";

    /// <summary>What most people don't know, and the one thing the welcome makes stand out.</summary>
    public const string WelcomeTip = "Sending games to a Switch? No need to compress them first: the Switch app does it as it installs them.";

    public const string WelcomeWarning =
        "Compressed games need sigpatches and system version 12.0.0 or newer. Official updates won't install over them.";

    public const string WelcomeLater = "All of this can be changed later, under Options.";

    /// <summary>What About says it is, on every version: the GUI's and Android's About, and the home screen's Help and --help.</summary>
    public const string License = "Free software under the GNU General Public License, version 2.";

    /// <summary>Asked the once, of whoever had the welcome before sending problems was on it.</summary>
    public const string ProblemsQuestion = "Send problem reports?";

    public const string ProblemsExplained = "When something goes wrong, a report can go to the developer by itself, so it can be fixed. Your keys are never in it. It can be changed later, under Options.";

    /// <summary>What a report has in it, said where one is sent from.</summary>
    public const string ReportHolds = "the version, your settings and the games on the list, with no keys, all shown below";
}

/// <summary>A wrapped help text, laid out for whatever width the window has right now.</summary>
internal static class HelpText
{
    private const int FlagColumn = 26;

    public static List<(string text, UiLevel level)> Lines(int width)
    {
        var lines = new List<(string, UiLevel)>();
        void Heading(string text)
        {
            lines.Add(("", UiLevel.Info));
            lines.Add((text, UiLevel.Heading));
        }
        void Paragraph(string text)
        {
            foreach (string row in Terminal.Wrap(text, width))
                lines.Add((row, UiLevel.Info));
        }
        void Command(string command, string text)
        {
            foreach (string row in Terminal.Wrap($"  {command}", width, 4))
                lines.Add((row, UiLevel.Info));
            foreach (string row in Terminal.Wrap($"    {text}", width, 4))
                lines.Add((row, UiLevel.Detail));
        }
        void Flag(string flag, string text)
        {
            int column = Math.Min(FlagColumn, Math.Max(width / 3, 12));
            bool fits = flag.Length + 2 < column;
            string start = fits ? flag.PadRight(column) : flag;
            if (!fits)
                lines.Add((flag, UiLevel.Info));
            string body = fits ? start + text : new string(' ', column) + text;
            foreach (string row in Terminal.Wrap(body, width, column))
                lines.Add((row, UiLevel.Info));
        }

        lines.Add(("Usage", UiLevel.Heading));
        Command("hacbrewpacklz", "Open the home screen when double clicked, or build an NSP from folders in a command window.");
        Command("hacbrewpacklz compress [options] <file or folder>...", "Rebuild NSP, NSZ, XCI or XCZ files as compressed NSPs. convert does the same.");
        Command("hacbrewpacklz analyze [options] <file or folder>...", "Show how large the result would be, without writing anything. With no inputs, the folders of a build.");
        Command("hacbrewpacklz extract [options] <file or folder>...", "Extract NSP, NSZ, XCI or XCZ files to folders.");
        Command("hacbrewpacklz check [options] <file or folder>...", "Check games for damage by reading all of each back through its hashes, writing nothing, as the Switch checks a game.");
        Command("hacbrewpacklz details [-k <keyset>] [--measure] <game file> [its update and DLC]...", "What there is to know about a game, as Details shows it on the screens: what compressing it would make of it, what's in it, the file and what it needs.");
        Command("hacbrewpacklz look [-k <keyset>] <game file> [folder or file inside it] [--all] [--save [--to <folder>]]", "What's inside a game, a folder at a time, as Look inside shows it: a file is shown as text, said to be a picture, or shown as its first bytes. --all lists every file under it, a line each. --save saves it, or all of the game, to Files from games beside the game, or in --to.");
        Command("hacbrewpacklz verify -k <keyset> <file.nsp> [--romfsdir <dir>] [--exefsdir <dir>]", "Check a finished NSP, and compare it with folders if given.");
        Command("hacbrewpacklz send <file or folder>...", "Share NSP, NSZ, XCI or XCZ files, or folders of games, with hacBrewPackLZ on a Switch, over the network or a USB cable, so it can install them. Takes no options. Runs until Ctrl+C.");
        Command("hacbrewpacklz menu", "Open the home screen. Running it with nothing after it does too.");
        Command("hacbrewpacklz update", "Install the latest version, if it's newer than this one.");
        Command("hacbrewpacklz report [what you were doing]", "Send a report to the developer: the version, the system and the settings, with what you say and no keys. If it can't be sent, it's saved on your desktop instead.");

        Heading("About");
        Paragraph($"{AppInfo.Name} {AppInfo.Version}. {OptionCatalog.WelcomeIntro} {OptionCatalog.License}");
        lines.Add(("", UiLevel.Info));
        Paragraph("Builds an installable NSP like hacBrewPack, with the RomFS section of the Program NCA stored LZ4 compressed through the NCA compression layer. Input files and folders are never modified.");
        lines.Add(("", UiLevel.Info));
        Paragraph("Dropping game files or folders onto the program opens them on the home screen. Put prod.keys beside the program, or use -k.");

        Heading("Converting, analyzing and extracting");
        Paragraph("Folders are searched for .nsp, .nsz, .xci and .xcz files, including subfolders, and for games extracted to folders, which convert builds into NSPs as the home screen does. An update given with its base game is applied to it, so the result is one title: the newest update wins and a repacked title holds no ticket. A game's DLC goes into its file, and DLC given without their game go together into one file, unless --separatedlc asks for a file for each. An update without its base game can't be processed. Output goes beside the input unless you choose otherwise.");
        lines.Add(("", UiLevel.Info));
        Flag("--only <list>", "Only these titles, by title ID or part of the name, separated by commas");
        Flag("--skip <list>", "Leave these titles out, by title ID or part of the name, separated by commas");

        foreach (var group in OptionCatalog.Groups)
        {
            Heading(group.Title);
            foreach (var option in group.Options)
                Flag(option.Short is null ? option.Flag : $"{option.Short}, {option.Flag}", option.CommandHelp ?? option.Help);
        }

        Heading("Other");
        Flag("-h, --help", "Display this help");
        Flag("--backupdir", "Accepted for compatibility and ignored, source files are never modified");
        return lines;
    }

    public static string Plain(int width)
    {
        var text = new StringBuilder();
        foreach (var (line, _) in Lines(width))
            text.AppendLine(line);
        return text.ToString();
    }
}

/// <summary>The choices made on the console screen or in the GUI, kept between runs as ordinary command line options.</summary>
internal sealed class Settings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private static string FilePath =>
        Environment.GetEnvironmentVariable("HACBREWPACKLZ_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name, "settings.txt");

    /// <summary>Where the settings are kept, which is where anything else this keeps for itself goes too.</summary>
    public static string Folder => Path.GetDirectoryName(Path.GetFullPath(FilePath))!;

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            foreach (string line in File.ReadLines(FilePath))
            {
                int eq = line.IndexOf('=');
                string flag = eq > 0 ? line[..eq] : line;
                string value = eq > 0 ? line[(eq + 1)..] : "1";
                // What only the command line goes by is dropped, should an earlier version have saved it.
                if (flag is WelcomedKey or ProblemsAskedKey or ViewKey || (OptionCatalog.AllOptions.Any(o => o.Flag == flag) && !OptionCatalog.CommandLineOnly.Contains(flag)))
                    settings._values[flag] = value;
                // Hide compressed games, which said which view the list started on, has given way to the view it was last on: turned off, the
                // list started on everything, and does the once more.
                else if (flag == "--showcompressed" && value == "1")
                    settings._values.TryAdd(ViewKey, "everything");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No settings yet, or they can't be read: start from the defaults.
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Written beside the old one and then put in its place, as on the Switch: written over in place, the power going or the program
            // being stopped in that moment left it empty, or cut off part of the way through a folder's name.
            string fresh = FilePath + ".new";
            using (var file = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
            {
                foreach (var (flag, value) in _values)
                    writer.WriteLine($"{flag}={value}");
                writer.Flush();
                file.Flush(true);
            }
            File.Move(fresh, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Ui.Warn($"Couldn't save the settings: {e.Message}");
        }
    }

    /// <summary>The value as entered, with a toggle's being "1" when its flag is on. Empty means the default.</summary>
    public string Get(string flag) => _values.GetValueOrDefault(flag, "");

    /// <summary>What the screens say under an option: for a part of a file name, with what a game's name is like as they're ticked now.</summary>
    public string HelpOf(OptionSpec option)
    {
        if (!OptionCatalog.Names.Options.Contains(option))
            return option.Help;
        // Each is on while its flag isn't.
        var parts = new NameParts(Get("--noidinname") != "1", Get("--noversioninname") != "1", Get("--nodlccountinname") != "1", Get("--nolz4inname") != "1");
        return $"{option.Help}\nLike: {FileNames.ForGame("Horizon Chase Turbo", 0x01009EA00B714000, 1572864, 3, true, parts)}";
    }

    /// <summary>What's set, as it's saved.</summary>
    public IEnumerable<string> Lines() => _values.Select(v => $"{v.Key}={v.Value}");

    /// <summary>The welcome has been and gone. Kept beside the options, though it isn't one and never goes to a job. As sending problems to
    /// the developer is on the welcome, having been through it counts as having been asked about that too.</summary>
    public bool Welcomed
    {
        get => _values.ContainsKey(WelcomedKey);
        set
        {
            if (value)
            {
                _values[WelcomedKey] = "1";
                _values[ProblemsAskedKey] = "1";
            }
            else
            {
                _values.Remove(WelcomedKey);
            }
        }
    }

    /// <summary>
    /// Sending problems to the developer has been asked about: on the welcome, or the once, for whoever had the welcome before that was on
    /// it. Asked only if the welcome has been, as the welcome asks it itself.
    /// </summary>
    public bool ProblemsAsked
    {
        get => _values.ContainsKey(ProblemsAskedKey);
        set
        {
            if (value)
                _values[ProblemsAskedKey] = "1";
            else
                _values.Remove(ProblemsAskedKey);
        }
    }

    public bool AskAboutProblems => Welcomed && !ProblemsAsked;

    private const string WelcomedKey = "welcomed";
    private const string ProblemsAskedKey = "problemsasked";
    private const string ViewKey = "view";

    /// <summary>The view the list was last on, which it opens on next time, as on the Switch: "todo" to begin with, "done" or "everything".</summary>
    public string View
    {
        get => _values.GetValueOrDefault(ViewKey, "todo");
        set => _values[ViewKey] = value;
    }

    /// <summary>The answer to the question asked the once: sending them, or not, and not asking again.</summary>
    public void AnswerAboutProblems(bool send)
    {
        if (send)
            Set(ProblemReports.Flag, "1");
        ProblemsAsked = true;
        Save();
    }

    public void Set(string flag, string value)
    {
        if (value.Length == 0)
            _values.Remove(flag);
        else
            _values[flag] = value;
    }

    /// <summary>Parses just the options a job reads, so it reports problems in the same words as the command line.</summary>
    public Options ToOptions(OptionUse use) => Options.Parse(ToArguments(use));

    public string[] ToArguments(OptionUse use)
    {
        var args = new List<string>();
        foreach (var option in OptionCatalog.AllOptions)
        {
            if ((option.Use & use) == 0 || OptionCatalog.CommandLineOnly.Contains(option.Flag))
                continue;
            if (!_values.TryGetValue(option.Flag, out string? value))
            {
                if (!option.DefaultApplies)
                    continue;
                value = option.Default;
            }
            if (option.Kind != OptionKind.Toggle)
                args.Add($"{option.Flag}={value}");
            else if (value == "1")
                args.Add(option.Flag);
        }
        return args.ToArray();
    }
}
