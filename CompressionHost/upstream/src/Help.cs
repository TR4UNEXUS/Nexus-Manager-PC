using System.Text;

namespace HacBrewPackLz;

/// <summary>
/// A few lines on what people most often need to know, each with where to read the rest, what to try next when something fails,
/// and a report of what happened that can be sent to someone without any keys in it. The Switch app has the same, in app_help.cpp.
/// </summary>
internal static class Help
{
    public const string Page = "https://gitlab.com/ismaellozanoperezserrano/hacbrewpacklz/-/blob/main/docs/help.md";
    public const string NewIssue = "https://gitlab.com/ismaellozanoperezserrano/hacbrewpacklz/-/issues/new";
    public const string ReportFileName = "hacBrewPackLZ report.txt";

    public sealed record Topic(string Title, string Text, string Anchor)
    {
        public string Url => $"{Page}#{Anchor}";
    }

    /// <summary>The same topics as docs/help.md, which says more.</summary>
    public static readonly Topic[] Topics =
    [
        new("A game won't start",
            "Compressed games need sigpatches, and system version 12.0.0 or newer on the Switch. Without them, the console says the game is damaged or asks for a system update.\n\n" +
            "An official update installed over a compressed game stops it starting, as an update only fits the original game it was made for. The Switch app makes it part of the game, which is on Installed games there, to do. Or remove the update, and give it to this app along with the compressed game.\n\n" +
            "A compressed copy of a game that's only whole with its update can ask for an update and not start, though it's whole. The Switch app's overview says so, and Fix there puts it right. Or compress the original game and its update again, and install that.\n\n" +
            "A compressed copy of a game an installer had let start on an older system can ask for a system update. The overview says so too, and Fix puts that right as well.\n\n" +
            "If it's none of these, report the problem, with the game and the error code.",
            "a-game-wont-start"),
        new("Updating a compressed game",
            "An update has to be made part of a compressed game, which this app does. Add the compressed game and the new update here together and compress them, and they come out as one compressed game. On the Switch, the Switch app makes an update part of the installed game, whether it was installed the usual way or sent to it. The original game isn't needed, except for some games an older version of this app compressed, and for games their developer compressed, such as many newer ones. It says so when it is.\n\n" +
            "To stop the console updating games by itself, turn off Auto-Update Software in its System Settings, under System.",
            "updating-a-game"),
        new("A key is missing",
            "\"Not in the keyset\" means your keys are older than the game. Update the Switch's system version, run Lockpick_RCM again, and use the new prod.keys.",
            "missing-keys"),
        new("A ticket is missing",
            "\"Needs a title key\" or \"personalized ticket\" means the game's ticket isn't in the file. Use a copy of the game that has its ticket, which most dumping tools can include.",
            "missing-tickets"),
        new("Why some games barely shrink",
            "Many newer games are already packed tightly, or keep most of their data where compression doesn't reach. Analyze works out each game's saving before anything's done. One measured to save less than the minimum saving, in the options, goes under Done, saying why.",
            "games-that-barely-shrink"),
        new("Sending games to the Switch",
            "Use Send to Switch, and open hacBrewPackLZ on the Switch, on the same Wi-Fi. The games come up on its Game files: tick them and press X. A USB cable works too. From Windows, it needs the WinUSB driver from Zadig. From a Mac, it needs libusb: brew install libusb. From Linux, it needs libusb, which nearly every Linux has, and this rule, once, so your user can use the Switch: echo 'SUBSYSTEM==\"usb\", ATTRS{idVendor}==\"057e\", ATTRS{idProduct}==\"3000\", MODE=\"0666\"' | sudo tee /etc/udev/rules.d/70-hacbrewpacklz.rules, then sudo udevadm control --reload-rules && sudo udevadm trigger",
            "sending-games-to-the-switch"),
        new("Where things are",
            "Compressed games go beside the originals, unless the options say somewhere else. File names, in the options, says what they're called. What Look inside saves goes in Files from games, beside the game, or on Android where Save to… puts it. A report goes on the desktop, or is shared on Android.",
            "where-things-are"),
    ];

    /// <summary>What went wrong in a few plain words, for a list, where a failure as it's said, with its files, offsets and codes, doesn't tell
    /// people much. The failure itself is kept for where people go for the details, which <paramref name="whereItSays"/> points to when nothing
    /// plainer can be said. The same sentences as the Switch's (plainReason in switch/app/app_help.cpp).</summary>
    public static string PlainReason(string failure, string? whereItSays)
    {
        bool Has(string text) => failure.Contains(text, StringComparison.Ordinal);
        // The card's own trouble first, as some of it says "damaged" too.
        return Has("file system it was writing to is damaged") ? "The SD card's file system is damaged. Check it on a PC."
            : Has("(2001-0123)") ? "The SD card stopped answering. Open the app again."
            : Has("(2001-0106)") || Has("(2001-0105)") ? "The SD card was busy. Try again."
            // Renaming a file on the PC (Session.Rename), which says why in its own words before the system's. The Switch says a taken name by its
            // code, below.
            : Has("couldn't be renamed") && Has(", as another program has it open:") ? "It's open in another program. Close it, then try again."
            : Has("couldn't be renamed") && Has(", as changing it isn't allowed:") ? "Changing it isn't allowed. Check it isn't read-only."
            : Has("couldn't be renamed") && Has(", as another file has that name:") ? "Another file has that name already. Try again."
            : Has("couldn't be renamed") && Has(", as its new name is too long:") ? "The new name is too long there. Turn a part off under File names."
            : Has("couldn't be renamed") && Has(", as it isn't there any more:") ? "A file it needed isn't there any more."
            : Has("couldn't be renamed") ? (whereItSays is null ? "It couldn't be renamed. Try again." : $"It couldn't be renamed. {whereItSays}")
            // Before anything the firmware's own error, which follows, could be taken for. The words before 1.6 too, as a report from an older
            // version still says them.
            : Has("can't start compressed games yet") || Has("firmware won't read compressed games") ? "This console can't start compressed games yet."
            : Has("doesn't read back the same") ? "The SD card may be failing. Check it on a PC."
            : Has("differently from") ? "The new copy didn't come out right, so the original stays."
            : Has("isn't a readable NCA3") || Has("header_key") ? "Your keys can't open this game."
            : Has("not in your keys") || Has("not in the keyset") ? "Your keys are too old for this game."
            : Has("needs a title key") || Has("personalized ticket") ? "This game's ticket is missing."
            // Where an update laid over its game fails its hash check, which of the two it was (NcaFile.WhyMismatch). Where that can't be told,
            // it says it could be either, as every such failure before 1.5.3 did.
            : Has("so the game is damaged") ? "The game is damaged: dump or download it again."
            // An update whose table leaves part of its section out stops there, before any hash check: the update is damaged too.
            : Has("so the update is damaged") || Has("isn't covered by the update's table") ? "The update is damaged: download it again."
            : Has("made for a different copy of the game") && Has("not the original") ? "The update is for a different copy of the game: use the original game."
            : Has("made for a different copy of the game") ? "The update is for a different copy of the game: get the update for this one."
            : Has("doesn't belong to this base game") ? "Damaged, or the update is for a different copy: check both."
            // A game compressed, repacked or cut down that doesn't have a part of the original an update reads. Before 1.5 the error said "the
            // compressed copy", which a saved error can still say.
            : Has("compressed game doesn't have") || Has("repacked game doesn't have") || Has("cut-down game doesn't have") || Has("the compressed copy doesn't have")
                ? "This update needs the original game."
            : Has("enough room") || Has("not enough space") ? "There isn't enough free space."
            : Has("Couldn't tell how much room is left") ? "The console didn't say how much room is left. Try again."
            : Has("enough memory") || Has("enough free memory") ? "There isn't enough memory."
            : Has("can't be found any more") ? "The PC it was on stopped sharing it."
            // The system's own code, where the words don't say.
            : FileSystemCodeReason(failure) is { } coded ? coded
            // Anything else that stops a DLC going into its game's file (TitleConverter.Run). With the setting off, the game is made without it.
            : failure.StartsWith("Its DLC", StringComparison.Ordinal) ? "A DLC is damaged: get it again, or turn off Game and DLC in one file."
            : Has("damaged") || Has("hash check failed") || Has("doesn't match") || Has("lies outside") || Has("cut short") || Has("BKTR")
                || Has("ended earlier than expected") ? "Damaged: dump or download it again."
            : whereItSays is null ? "Something went wrong." : $"Something went wrong. {whereItSays}";
    }

    /// <summary>What the file system's own error code (2002-NNNN) in a failure means, where nothing in its words says: each number, or range of
    /// them, as the system's list of them has it. The first code with a meaning goes; null if there's none. As plainForFileSystemCode on the
    /// Switch.</summary>
    private static string? FileSystemCodeReason(string failure)
    {
        for (int at = failure.IndexOf("(2002-", StringComparison.Ordinal); at >= 0; at = failure.IndexOf("(2002-", at + 6, StringComparison.Ordinal))
        {
            if (at + 11 > failure.Length || failure[at + 10] != ')' || !int.TryParse(failure.AsSpan(at + 6, 4), System.Globalization.NumberStyles.None, null, out int code))
                continue;
            string? said = code switch
            {
                1 => "A file it needed isn't there any more.",
                2 => "Another file has that name already. Try again.",
                7 => "A file it needed was in use. Try again.",
                >= 30 and <= 45 => "There isn't enough free space.",
                1004 => "This game's ticket is missing.",
                >= 2000 and <= 2499 => "The SD card couldn't be reached. Check it's in properly.",
                >= 2500 and <= 2999 => "The game card couldn't be read. Check it's in properly.",
                >= 3200 and <= 3499 => "There isn't enough memory.",
                >= 3500 and <= 3999 => "The console's own storage couldn't be reached. Restart it.",
                // The SD card's FAT itself, before the rest of what says data is corrupted.
                >= 4681 and <= 4699 => "The SD card's file system is damaged. Check it on a PC.",
                >= 4000 and <= 4999 => "Damaged: dump or download it again.",
                _ => null,
            };
            if (said is not null)
                return said;
        }
        return null;
    }

    /// <summary>What's worth trying next, after a failure people can do something about. Just the failure, otherwise.</summary>
    public static string WithNextStep(string failure)
    {
        bool Has(string text) => failure.Contains(text, StringComparison.Ordinal);
        string? next =
            Has("isn't a readable NCA3") || Has("header_key") ? "Your keys may not be your console's, or may be damaged: dump them again with Lockpick_RCM."
            : Has("not in the keyset") || Has("not in your keys") ? "Update the Switch's system version, then dump your keys again with Lockpick_RCM."
            : Has("needs a title key") || Has("personalized ticket") ? "Use a copy of the game that has its ticket."
            : Has("so the game is damaged") ? "Dump or download the game again."
            : Has("so the update is damaged") || Has("isn't covered by the update's table") ? "Download the update again."
            : Has("made for a different copy of the game") ? Has("not the original") ? "Give the original game, or the earlier update's file too." : "Get the update made for this one."
            : Has("doesn't belong to this base game") ? "If the game was compressed or repacked with an earlier update in it, give that update's file too, or the original game."
            : Has("BKTR") || Has("damaged") || Has("doesn't match") || Has("lies outside the file") || Has("cut short")
                ? "Dump or download it again, and check that an update is for this game's version."
            : Has("enough free memory") ? "Close other programs and try again."
            : Has("Something unexpected went wrong") ? "Please report it, with Report a problem."
            : null;
        return next is null || Has(next) ? failure : $"{failure.TrimEnd('.', ' ')}. {next}";
    }

    /// <summary>Every key's value taken out, whatever the case it's in. Nothing here writes keys down, but a report goes to someone
    /// else, so it's made sure of. The user's own folder is shortened too, as it holds their name.</summary>
    public static string Clean(string text, Keyset? keyset)
    {
        if (keyset is not null)
        {
            var secrets = keyset.Secrets().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lengths = secrets.Select(s => s.Length).Distinct().OrderBy(l => l).ToArray();
            var output = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (!Uri.IsHexDigit(text[i]))
                {
                    output.Append(text[i++]);
                    continue;
                }
                int end = i;
                while (end < text.Length && Uri.IsHexDigit(text[end]))
                    end++;
                string run = text[i..end];
                bool secret = lengths.Any(length => Enumerable.Range(0, Math.Max(run.Length - length + 1, 0)).Any(at => secrets.Contains(run.Substring(at, length))));
                output.Append(secret ? "[key left out]" : run);
                i = end;
            }
            text = output.ToString();
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 3 ? text.Replace(home, "~", StringComparison.OrdinalIgnoreCase) : text;
    }

    /// <summary>Somewhere else for reports to go, for testing, so that a test leaves the desktop alone.</summary>
    private const string FolderVariable = "HACBREWPACKLZ_REPORT_FOLDER";

    /// <summary>Saved where people will find it: on the desktop, or in their own folder when there isn't one.</summary>
    public static string SaveReport(string text)
    {
        string folder = Environment.GetEnvironmentVariable(FolderVariable) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (folder.Length == 0 || !Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(folder, ReportFileName);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Shown in the file manager, other than in a test, which would leave a window open on someone's screen.</summary>
    public static void ShowReport(string path)
    {
        if (Environment.GetEnvironmentVariable(FolderVariable) is null)
            Explorer.Show(path);
    }
}
