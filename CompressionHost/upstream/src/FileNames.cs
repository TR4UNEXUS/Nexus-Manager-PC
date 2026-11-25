namespace HacBrewPackLz;

/// <summary>Which parts a game's file name has after the game's name. All of them to begin with.</summary>
internal readonly record struct NameParts(bool TitleId = true, bool Version = true, bool DlcCount = true, bool Lz4 = true);

/// <summary>What a game's new file is called: its name, then each part that's ticked, in brackets. The Switch names them the same way
/// (fileNameForGame).</summary>
internal static class FileNames
{
    public static string ForGame(string gameName, ulong titleId, uint version, int dlcCount, bool compressed, NameParts parts)
    {
        string brackets = (parts.TitleId ? $"[{titleId:X16}]" : "") + (parts.Version ? $"[v{version}]" : "")
            + (parts.DlcCount && dlcCount > 0 ? $"[{dlcCount}DLC]" : "") + (parts.Lz4 && compressed ? "[LZ4]" : "");
        string name = LocalNames.ForOutputName(gameName);
        // Windows takes Con.nsp for a device, and ".nsp" is no name at all, so such a name has the title ID whatever's ticked.
        if (brackets.Length == 0 && (name.Length == 0 || LocalNames.IsDeviceName(name)))
            brackets = $"[{titleId:X16}]";
        return string.Join(" ", new[] { name, brackets }.Where(s => s.Length > 0)) + ".nsp";
    }
}
