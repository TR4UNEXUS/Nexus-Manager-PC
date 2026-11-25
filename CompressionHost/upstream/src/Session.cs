using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HacBrewPackLz;

internal enum SessionActivity
{
    Idle,
    Reading,
    Analyzing,
    /// <summary>Reading all of each game a first look can't vouch for, writing nothing.</summary>
    Measuring,
    Compressing,
    Extracting,
    Verifying,
    Sending,
}

internal enum RowState
{
    Ready,
    Working,
    Done,
    Failed,
}

internal sealed record TitleRow(string Key, string Name, string Detail, long InputBytes, string[] Files)
{
    public SizeEstimate? Estimate { get; init; }

    /// <summary>For a game packed tighter than compressing packs, as an NSZ or XCZ is: its files themselves. <see cref="InputBytes"/> is then
    /// what it takes installed as it is, which any installer unpacks it to, as what's made of it goes on the console compressed as it is.</summary>
    public long PackedBytes { get; init; }

    /// <summary>What its files come to as they are.</summary>
    public long FileBytes => PackedBytes > 0 ? PackedBytes : InputBytes;

    public RowState State { get; init; }
    public BatchMode DoneAs { get; init; }
    public long OutputBytes { get; init; }
    public string? OutputPath { get; init; }

    /// <summary>For a compressed copy, what compressing it freed as the copy itself says, since 1.5: 0 where it doesn't say.</summary>
    public long Smaller { get; init; }

    /// <summary>What compressing a copy freed, from the copy's own word or from analyzing it: 0 where neither knows.</summary>
    public long Freed => Math.Max(Smaller, Estimate?.FreedBytes ?? 0);
    public string? Failure { get; init; }

    /// <summary>There's an update to make part of it, which is what repacking gains anything from.</summary>
    public bool HasUpdate { get; init; }

    /// <summary>The line under it where there's only room for a few words, as on a phone: whether its update is in it, without which version
    /// it is, which its page says. Empty where it's the same as <see cref="Detail"/>.</summary>
    public string ShortDetail { get; init; } = "";

    /// <summary>A game laid out in folders, which is built into an NSP rather than converted, and is already as extracted as it gets.</summary>
    public bool IsFolder { get; init; }

    /// <summary>The picture the Switch shows for it, as the JPEG it's kept as.</summary>
    public byte[]? Icon { get; init; }

    /// <summary>What there is to know about it that isn't the title itself, such as other copies of it that were left aside.</summary>
    public string[] Notes { get; init; } = [];

    /// <summary>All of it is what this tool already made, as new as what it was made from.</summary>
    public bool AlreadyCompressed { get; init; }

    /// <summary>Its developer or another tool compressed it, as its headers say, and compressing wouldn't leave it as it is: made again, its
    /// updates would then need the original game. So it's only ticked by hand, and analyzing gives it a figure all the same.</summary>
    public bool ByMakers { get; init; }

    /// <summary>Beside this tool's copy of its game, a DLC of it has no file of its own, such as one only inside a merged original, which
    /// compressing gives one with Game and DLC in one file off. Until then it's to do, whatever that saves.</summary>
    public bool DlcWithoutFile { get; init; }

    /// <summary>This tool's copy of its game, whose file is made again to hold a DLC of it that's here, as on the Switch: what it comes to keeps
    /// the copy as it is, and ticked, its file is made.</summary>
    public bool DlcNotInFile { get; init; }

    /// <summary>There's nothing for compressing to do: it already was, or all of it is stored that way.</summary>
    public bool IsCompressed => !DlcWithoutFile && !DlcNotInFile && (AlreadyCompressed || Estimate?.AlreadyCompressed == true);

    /// <summary>Whether jobs include this title. Everything starts out included.</summary>
    public bool Selected { get; init; } = true;

    /// <summary>Someone ticked or unticked it themselves, and from then on that's what counts.</summary>
    public bool ChosenByHand { get; init; }

    /// <summary>Why the minimum saving would leave this title out, if it would.</summary>
    public string? BelowMinimum { get; init; }

    /// <summary>A first look can't vouch for it, and only reading all of it can say: a thorough analysis does that, writing nothing, and ticks
    /// it if it's worth it. It isn't ticked until then, as that read can take a long time and usually finds too little. Compressing only does
    /// what's ticked. See <see cref="SavingRule.NotSure"/>.</summary>
    public bool NotSure { get; init; }

    /// <summary>What it's sure to come to at most, as totals count it. One that isn't sure yet counts as staying as it is, which is the most it
    /// comes to: it's only compressed if that saves enough.</summary>
    public long? SureAfterBytes => Estimate is not { } estimate ? null : NotSure ? estimate.InputBytes : estimate.MostBytes;

    /// <summary>The finished size if there is one, or else the estimate.</summary>
    public long? AfterBytes =>
        State == RowState.Done && DoneAs == BatchMode.Convert ? OutputBytes : Estimate?.OutputBytes;

    public bool IsDone(BatchMode mode) => State == RowState.Done && DoneAs == mode;

    /// <summary>
    /// Ticked, and compressing would still do something with it: it isn't done, and isn't compressed already. With
    /// compression off, a compressed game is repacked like any other.
    /// </summary>
    public bool LeftToCompress(bool compressing) => Selected && !IsDone(BatchMode.Convert) && !(compressing && IsCompressed);

    public bool NeedsEstimate => Estimate is null && !IsDone(BatchMode.Convert);

    /// <summary>What Analyze works out: what's ticked, and a game its developer compressed that's unticked only for that, so it has a figure too.</summary>
    public bool ToAnalyze => (Selected || (ByMakers && !ChosenByHand)) && NeedsEstimate;

    /// <summary>What kind of title this is, and why it was left out if it was, or that it's measured first, or what making a compressed game
    /// again comes to. What it saves has a column of its own.</summary>
    public string DetailWithEstimate => BelowMinimum is { } reason ? $"{Detail}, {reason}" : AgainNote.Length > 0 ? $"{Detail}, {AgainNote}"
        : NotSure ? $"{Detail}, {NotSureShort}" : Detail;

    /// <summary>For a game compressed before that analyzing finds worth compressing again, what that comes to, in a few words: smaller, or for
    /// one its developer or another tool compressed, that its updates would then need the original game, which is why it isn't ticked by
    /// itself. Who compressed it is for Details and the log. Empty for anything else.</summary>
    public string AgainNote { get; init; } = "";

    public const string SmallerAgain = "smaller if compressed again";
    public const string UpdatesNeedOriginal = "its updates would then need the original game";

    /// <summary>Why compressing leaves it out when it isn't ticked by hand, if it does: the minimum saving, its being compressed already, or
    /// that its updates would then need the original game.</summary>
    public string? LeftOut => BelowMinimum ?? (IsCompressed ? "already compressed" : AgainNote == UpdatesNeedOriginal && !ChosenByHand ? AgainNote : null);

    /// <summary>What a list says beside a game a first look can't vouch for, where there's only room for a few words.</summary>
    public const string NotSureShort = "not sure yet, may save more: Thorough analysis reads it in full";

    /// <summary>The same where there's room to say it all.</summary>
    public const string NotSureLong = "A first look can't tell what this game saves, and it may save more than it seems, so it isn't ticked. Thorough analysis, in the options, reads it in full after the first looks, writing nothing, and ticks it if it saves at least the minimum. Ticked by hand, it's compressed whatever it saves.";
}

internal sealed record LogLine(UiLevel Level, string Text);

/// <summary>What the list shows, as on the Switch: what's still to do, what's done, or everything. Done is what's compressed: what already
/// was when it was found, as when the copy this tool made is beside the original, and what a job compressed, once the list is gone back to.
/// A compressed file chosen on its own is there to be sent or installed, so it's to do. With compression off nothing is done, as on the
/// Switch, so there's no Done.</summary>
internal enum GamesShown { ToDo, Done, Everything }

/// <summary>A job's own view: its games, which are the rows while it shows, how far it's got, and what they come to. It stays after the job,
/// as what came of it, until the list is gone back to.</summary>
/// <param name="Done">How many of its games it did. <paramref name="Failed"/> couldn't be done, and <paramref name="LeftOut"/> weren't worth it,
/// or were compressed already.</param>
/// <param name="AfterBytes">What the games come to by the estimates there were when it started, when every one of them had one.</param>
/// <param name="Overall">How far through all of it, by the size of the games. It only ever goes forward.</param>
/// <param name="SavedBytes">What the games compressed so far saved.</param>
/// <param name="Said">Once it's over, what to know about what it made: where the new files are, and what goes with them.</param>
internal sealed record JobSnapshot(SessionActivity Kind, bool Repacking, bool Running, bool Stopped, int Count, int Done, int Failed, int LeftOut,
    long BeforeBytes, long? AfterBytes, double Overall, double? SecondsLeft, long SavedBytes, TimeSpan Elapsed, string[] Said)
{
    /// <summary>"Compressing", "Repacking", "Extracting" or "Checking".</summary>
    public string Doing => Kind == SessionActivity.Extracting ? "Extracting" : Kind == SessionActivity.Verifying ? "Checking" : Repacking ? "Repacking" : "Compressing";

    /// <summary>"Compressed", "Repacked", "Extracted" or "Checked".</summary>
    public string Did => Kind == SessionActivity.Extracting ? "Extracted" : Kind == SessionActivity.Verifying ? "Checked" : Repacking ? "Repacked" : "Compressed";

    public string Games => Count == 1 ? "1 game" : $"{Count} games";

    /// <summary>All of it was done, some of it, or none of it, which says what colour what it did is in.</summary>
    public UiLevel Outcome => Done == Count ? UiLevel.Success : Failed > 0 && Done == 0 ? UiLevel.Error : UiLevel.Warning;

    /// <summary>What it's doing to how many games, or did, and a word more: what they come to while it runs, how long it took and what wasn't
    /// done once it's over. <paramref name="to"/> goes between sizes, as an arrow where the font has one.</summary>
    public (string what, string more) Headline(string to)
    {
        if (Running)
            return ($"{Doing} {Games}", AfterBytes is { } after
                ? $"{ProgressReporter.Format(BeforeBytes)} {to} about {ProgressReporter.Format(after)}" : ProgressReporter.Format(BeforeBytes));
        string what = Done == Count ? $"{Did} {Games}" : Done == 0 ? $"Nothing was {Did.ToLowerInvariant()}" : $"{Did} {Done} of {Games}";
        var more = new List<string>();
        if (Done > 0)
            more.Add($"in {ProgressReporter.FormatElapsed(Elapsed)}");
        if (Failed > 0)
            more.Add($"{Failed} couldn't be done");
        if (LeftOut > 0)
            more.Add($"{LeftOut} left out");
        int notDone = Count - Done - Failed - LeftOut;
        if (Stopped && notDone > 0)
            more.Add($"{notDone} not done");
        return (what, string.Join(", ", more));
    }

    /// <summary>All together while it runs: how long the rest takes and what's been saved so far.</summary>
    public string Progress => string.Join(" \u00B7 ", new[]
    {
        SecondsLeft is { } seconds ? ProgressReporter.TimeLeft(seconds) : "working out how long it takes",
        SavedBytes > 0 && !Repacking ? $"{ProgressReporter.Format(SavedBytes)} saved so far" : "",
    }.Where(s => s.Length > 0));
}

internal sealed record SessionSnapshot(
    SessionActivity Activity,
    TitleRow[] Rows,
    InputIssue[] Issues,
    string[] Notes,
    LogLine[] Log,
    ProgressSnapshot? Progress,
    string? KeyProblem,
    string? CurrentTitle,
    int CurrentIndex,
    int CurrentCount,
    string? Summary,
    UiLevel SummaryLevel,
    string? LastOutput,
    string? UpdateNote,
    SendSnapshot? Send,
    GamesShown View,
    int ToDoCount,
    int DoneCount,
    int EverythingCount,
    GamesShown[] ViewsOffered,
    JobSnapshot? Job)
{
    /// <summary>A job's own view is up, from when it starts until the list is gone back to: the rows are only its games.</summary>
    public bool ShowingJob => Job is not null;

    public int CountOf(GamesShown view) => view switch { GamesShown.Done => DoneCount, GamesShown.Everything => EverythingCount, _ => ToDoCount };

    /// <summary>How many of the rows shown Done's summary counts as compressed, in every version: a copy that says what compressing it freed
    /// counts, even when it's settled as nothing to gain, as the Switch's overview counts it.</summary>
    public int CompressedCount => Rows.Count(r => r.IsCompressed || r.IsDone(BatchMode.Convert) || r.Freed > 0);

    public bool IsBusy => Activity != SessionActivity.Idle;

    /// <summary>What reading is on, such as "Opening game files: 3 of 12".</summary>
    public string ReadingText => CurrentTitle is null ? "Reading files" : CurrentCount > 0 ? $"{CurrentTitle}: {CurrentIndex} of {CurrentCount}" : CurrentTitle;
}

/// <summary>
/// What the console screen and the GUI have in common: the list of inputs, and one job at a time running in the
/// background. Each of them only shows snapshots of this and calls the methods below.
/// </summary>
internal sealed class Session : IUiSink, IDisposable
{
    private const int MaxLogLines = 4000;

    private readonly object _gate = new();
    private readonly List<string> _files = [];
    private readonly List<string> _folders = [];
    private readonly Dictionary<string, GameFolder> _gameFolders = [];
    private readonly List<string> _queued = [];
    /// <summary>Game files chosen one by one, rather than found in a folder. They're listed even when compressed games are hidden.</summary>
    private readonly HashSet<string> _picked = new(LocalNames.PathComparer);
    private readonly List<InputIssue> _expansionIssues = [];
    private readonly List<string> _notes = [];
    private readonly List<LogLine> _log = [];
    private List<string> _titleNotes = [];
    /// <summary>What each row is made of: a game with its update, and its DLC. A game folder has none.</summary>
    private readonly Dictionary<string, List<TitleJob>> _jobs = [];
    private GamesShown _view;
    /// <summary>The games in the last compress or extract job, in the order it did them, and whether its own view is up.</summary>
    private List<string> _jobKeys = [];
    private bool _jobShown;
    private SessionActivity _jobKind;
    private bool _jobRepacking;
    /// <summary>What each game a check went through was before it, which it goes back to once the list is gone back to: a check says what it
    /// found on its own view, and never undoes what another job did.</summary>
    private Dictionary<string, TitleRow> _checkedBefore = [];
    private long _jobBefore;
    private long? _jobAfter;
    private readonly Stopwatch _jobClock = new();
    /// <summary>How far the job has got: the size of the games it's been through, and how much of the one it's on has been compressed or
    /// written, across all its parts.</summary>
    private long _jobFinishedBytes;
    private long _rowBytes;
    private long _rowWork;
    private string? _workLabel;
    private long _workLastDone;
    private double _overallShown;
    private List<TitleRow> _rows = [];
    private List<InputIssue> _issues = [];
    private TitleSet? _set;
    private Keyset? _keyset;
    private string? _keyProblem;

    /// <summary>Files where NSPs go that couldn't be opened when the list was read, which another program holds. Any of them might be a copy of a
    /// game on the list, so nothing is compressed until they're free.</summary>
    private List<string> _heldCopies = [];
    private SessionActivity _activity;
    private ProgressSnapshot? _progress;
    private string? _currentTitle;
    private int _currentIndex;
    private int _currentCount;
    private string? _summary;
    private UiLevel _summaryLevel;
    private string? _lastOutput;
    private CancellationTokenSource? _stop;
    private Thread? _worker;
    private bool _reloadNeeded;
    private bool _bestFirst;
    private int _leftOut;
    private string? _updateNote;
    private bool _updating;
    private SendWatch? _sendWatch;

    public Settings Settings { get; } = Settings.Load();

    public Session()
    {
        _view = StartingView();
        if (Settings.Get("--noautoupdate") != "1")
            CheckForUpdates(quietly: true);
    }

    /// <summary>The view the list was last on, as on the Switch: to do, the first time.</summary>
    private GamesShown StartingView() => Settings.View switch { "done" => GamesShown.Done, "everything" => GamesShown.Everything, _ => GamesShown.ToDo };

    /// <summary>On a thread of its own, as it has nothing to do with whatever else is going on.</summary>
    public void CheckForUpdates(bool quietly = false)
    {
        lock (_gate)
        {
            if (_updating)
                return;
            _updating = true;
            if (!quietly)
                _updateNote = "Looking for a newer version...";
        }
        Notify();
        var thread = new Thread(() =>
        {
            var (updated, message) = Updater.Run();
            lock (_gate)
            {
                _updating = false;
                if (updated || !quietly)
                    _updateNote = message;
            }
            Notify();
        })
        { IsBackground = true, Name = "Updating" };
        thread.Start();
    }

    /// <summary>Raised on any thread. Keep it short: just note that the screen needs drawing again.</summary>
    public event Action? Changed;

    public TimeSpan ProgressInterval => TimeSpan.FromMilliseconds(100);

    /// <summary>What happened, for someone else to look at: the version, the system, the settings, every game on the list and
    /// what's been said, with no keys in it.</summary>
    public string ReportText()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{AppInfo.Name} {AppInfo.Version}, a report of what happened");
        text.AppendLine($"Made {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        text.AppendLine();
        text.AppendLine("== Settings");
        foreach (string line in Settings.Lines())
            text.AppendLine(line);
        Keyset? keyset;
        lock (_gate)
        {
            keyset = _keyset;
            text.AppendLine();
            text.AppendLine("== The list");
            foreach (var row in _rows)
            {
                text.Append($"{row.Name}  {row.Detail}  {row.State.ToString().ToLowerInvariant()}, {ProgressReporter.Format(row.InputBytes)}");
                if (row.Estimate is { } estimate)
                    text.Append($", {ProgressReporter.Format(estimate.OutputBytes)} after");
                if (!row.Selected)
                    text.Append(", not ticked");
                text.AppendLine();
                foreach (string said in new[] { row.Failure, row.BelowMinimum }.OfType<string>().Concat(row.Notes).Concat(row.Files))
                    text.AppendLine($"    {said}");
            }
            foreach (var issue in _issues.Concat(_expansionIssues))
                text.AppendLine($"Couldn't be added: {issue.Message}");
            foreach (string note in _notes.Concat(_titleNotes))
                text.AppendLine($"Note: {note}");
            text.AppendLine();
            text.AppendLine($"== The last {_log.Count} lines of what's been said");
            foreach (var line in _log)
                text.AppendLine(line.Text);
        }
        return Help.Clean(text.ToString(), keyset);
    }

    /// <summary>What it's doing, in a few words, for a note on the app not answering.</summary>
    public string Doing()
    {
        lock (_gate)
        {
            string what = _activity == SessionActivity.Idle ? "nothing in particular" : _activity.ToString().ToLowerInvariant();
            return _workLabel is { Length: > 0 } step ? $"{what}, {step}" : what;
        }
    }

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            var shown = _jobShown ? JobRowsLocked() : ListRowsLocked();
            GamesShown view = ViewShownLocked();
            int done = _rows.Count(AlreadyDoneLocked);
            // What going through folders turned up that can't be used is only listed among everything, or with what's to do when asked for.
            // With no game listed at all, they're shown anyway: hidden, they'd leave an empty list that says nothing about them.
            bool showUnusable = Settings.Get("--showunusable") == "1" || _rows.Count == 0;
            var issues = _jobShown ? [] : _issues.Concat(_expansionIssues).Where(i => view == GamesShown.Everything || (view == GamesShown.ToDo && (showUnusable || !i.FoundInFolder))).ToArray();
            return new SessionSnapshot(_activity, shown.ToArray(), issues, _notes.Concat(_titleNotes).ToArray(), _log.ToArray(),
                _progress, _keyProblem, _currentTitle, _currentIndex, _currentCount, _summary, _summaryLevel, _lastOutput, _updateNote, _sendWatch?.Snapshot(),
                view, _rows.Count - done, done, _rows.Count, ViewsOfferedLocked(), _jobShown ? JobLocked() : null);
        }
    }

    /// <summary>The rows of the view the list is on. While sending, that's everything, as everything is being shared.</summary>
    private List<TitleRow> ListRowsLocked()
    {
        GamesShown view = ViewShownLocked();
        return _rows.Where(r => view == GamesShown.Everything || (view == GamesShown.Done) == AlreadyDoneLocked(r)).ToList();
    }

    /// <summary>The job's games, in the order it does them. Once it's over, those that couldn't be done come first, then any it left out or
    /// didn't get to, each saying why, and then what each of the rest came to.</summary>
    private List<TitleRow> JobRowsLocked()
    {
        var byKey = _rows.ToDictionary(r => r.Key);
        var rows = _jobKeys.Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();
        if (JobRunningLocked())
            return rows;
        return rows.OrderBy(r => r.State == RowState.Failed ? 0 : r.State == RowState.Done ? 2 : 1).ToList();
    }

    private bool JobRunningLocked() => _jobShown && _activity == _jobKind;

    private bool CompressingLocked() => Settings.Get("--nocompress") != "1";

    private GamesShown ViewShownLocked() => _activity == SessionActivity.Sending ? GamesShown.Everything : _view;

    /// <summary>To do, Done and Everything, as on the Switch, with compression on or off.</summary>
    private GamesShown[] ViewsOfferedLocked() => [GamesShown.ToDo, GamesShown.Done, GamesShown.Everything];

    /// <summary>
    /// Nothing left to do with it: what a job here did, and, compressing, what was compressed when it was found, and what's settled as not
    /// worth it, saying why: measured to save less than the minimum, or with nothing to gain. To do keeps what's worth doing and what isn't
    /// known yet, as on the Switch. With compression off, a repack only gains anything by making an update part of its game, so one in one
    /// piece already, given with no update, is done too: it was never updated, was merged by another tool, or was repacked here. One that was
    /// chosen on its own is what was asked for, ready to send, so it's to do.
    /// </summary>
    private bool AlreadyDoneLocked(TitleRow row) =>
        row.IsDone(BatchMode.Convert) || (!PickedOnItsOwnLocked(row) && (CompressingLocked() ? row.IsCompressed || SettledLocked(row) : NothingToGainRepacked(row)));

    /// <summary>Not worth compressing, and that's known: its figure says so, rather than a first look that can't vouch for it.</summary>
    private static bool SettledLocked(TitleRow row) => row.State == RowState.Ready && row.Estimate is not null && row.BelowMinimum is not null && !row.NotSure;

    private bool PickedOnItsOwnLocked(TitleRow row) => row.Files.Any(f => _picked.Contains(Path.GetFullPath(f)));

    /// <summary>In one piece, given with no update to make part of it, so a repack with compression off gains nothing. A folder still has an NSP
    /// to be made of it.</summary>
    private static bool NothingToGainRepacked(TitleRow row) => !row.HasUpdate && !row.IsFolder;

    private JobSnapshot JobLocked()
    {
        var rows = JobRowsLocked();
        bool running = JobRunningLocked();
        long saved = rows.Where(r => r.IsDone(BatchMode.Convert)).Sum(r => r.InputBytes - r.OutputBytes);
        double overall = OverallLocked(out double? secondsLeft);
        int done = rows.Count(r => r.State == RowState.Done);
        int failed = rows.Count(r => r.State == RowState.Failed);
        int leftOut = running ? 0 : rows.Count(r => r.State == RowState.Ready && r.LeftOut is not null);
        bool stopped = !running && _summaryLevel == UiLevel.Warning;
        return new JobSnapshot(_jobKind, _jobRepacking, running, stopped, _jobKeys.Count, done, failed, leftOut, _jobBefore, _jobAfter, overall, secondsLeft, saved,
            _jobClock.Elapsed, running ? [] : JobSaidLocked(rows));
    }

    /// <summary>What to know about what a job made once it's over: where the new files are, and that an official update can't go on top of a
    /// compressed game.</summary>
    private string[] JobSaidLocked(List<TitleRow> rows)
    {
        var made = rows.Where(r => r.State == RowState.Done).ToList();
        if (made.Count == 0 || _jobKind == SessionActivity.Verifying)
            return [];
        if (_jobKind == SessionActivity.Extracting)
            return _lastOutput is { } last && Path.GetDirectoryName(last) is { Length: > 0 } where ? [$"The extracted files are in {where}."] : [];
        string folder = Settings.Get("--nspdir");
        var said = new List<string> { folder.Length > 0 ? $"The new files are in {folder}." : "The new files are beside the originals." };
        if (!_jobRepacking && made.Any(r => !r.HasUpdate && !r.IsFolder))
            said.Add(TitleJob.UpdatesWontWork);
        return said.ToArray();
    }

    /// <summary>
    /// How far through the job, by the size of its games: those it's been through, and of the one it's on, as much as has been compressed or
    /// written in all its parts so far, which is most of the time a game takes. The checks after that are what's left of it, so it stops a
    /// little short until the game's done. Time left goes by how long it's taken so far.
    /// </summary>
    private double OverallLocked(out double? secondsLeft)
    {
        secondsLeft = null;
        if (_jobBefore <= 0)
            return _overallShown;
        bool running = JobRunningLocked();
        double current = running ? Math.Min(_rowWork, _rowBytes * 0.97) : 0;
        _overallShown = Math.Max(_overallShown, Math.Min((_jobFinishedBytes + current) / _jobBefore, 1));
        double seconds = _jobClock.Elapsed.TotalSeconds;
        if (running && _overallShown >= 0.01 && seconds >= 5)
            secondsLeft = seconds * (1 - _overallShown) / _overallShown;
        return _overallShown;
    }

    /// <summary>To do, Done or Everything, which the list opens on next time too, as on the Switch. Choosing one goes back to the list from a
    /// job that's over. While one runs, it's what's on show.</summary>
    public void SetView(GamesShown view)
    {
        lock (_gate)
        {
            if (JobRunningLocked() || (_view == view && !_jobShown))
                return;
            _view = view;
            LeaveJobViewLocked();
        }
        Settings.View = view switch { GamesShown.Done => "done", GamesShown.Everything => "everything", _ => "todo" };
        Settings.Save();
        Notify();
    }

    /// <summary>From what a job came to, back to the list, where what it compressed is now under Done, and what it couldn't do is still to do.</summary>
    public void BackToList()
    {
        lock (_gate)
        {
            if (!_jobShown || JobRunningLocked())
                return;
            LeaveJobViewLocked();
        }
        Notify();
    }

    /// <summary>The list changing, or anything else starting, is the end of a job's view that's over.</summary>
    private void EndJobViewLocked()
    {
        if (!JobRunningLocked())
            LeaveJobViewLocked();
    }

    /// <summary>Back to the list from a job's view: what a check went through is as it was before it.</summary>
    private void LeaveJobViewLocked()
    {
        _jobShown = false;
        if (_checkedBefore.Count == 0)
            return;
        _rows = _rows.Select(r => _checkedBefore.TryGetValue(r.Key, out var before)
            ? r with { State = before.State, DoneAs = before.DoneAs, OutputPath = before.OutputPath, OutputBytes = before.OutputBytes, Failure = before.Failure }
            : r).ToList();
        _checkedBefore = [];
    }

    /// <summary>Called as a job starts: its games, in the order it'll do them, are what's on show from now on.</summary>
    private void ShowJobLocked(SessionActivity kind, List<TitleRow> rows)
    {
        _jobKeys = rows.Select(r => r.Key).ToList();
        _jobShown = true;
        _jobKind = kind;
        _jobRepacking = kind == SessionActivity.Compressing && !CompressingLocked();
        _jobBefore = rows.Sum(r => r.InputBytes);
        _jobAfter = kind == SessionActivity.Compressing && rows.All(r => r.Estimate is not null) ? rows.Sum(r => r.Estimate!.OutputBytes) : null;
        _jobFinishedBytes = _rowBytes = _rowWork = 0;
        _workLabel = null;
        _overallShown = 0;
        _jobClock.Restart();
    }

    public void AddInputs(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            EndJobViewLocked();
            foreach (string path in paths)
            {
                if (!Directory.Exists(path) && Keyset.LooksLikeKeyFile(path))
                {
                    Settings.Set("--keyset", Path.GetFullPath(path));
                    Settings.Save();
                    _keyset = null;
                    _reloadNeeded = true;
                }
                else
                {
                    _queued.Add(path);
                    if (File.Exists(path))
                        _picked.Add(Path.GetFullPath(path));
                }
            }
        }
        StartReading();
    }

    public void Remove(TitleRow row)
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            EndJobViewLocked();
            _files.RemoveAll(f => row.Files.Contains(f, LocalNames.PathComparer));
            _folders.RemoveAll(f => row.Files.Contains(f, LocalNames.PathComparer));
            _rows.RemoveAll(r => r.Key == row.Key);
            _reloadNeeded = true;
        }
        StartReading();
    }

    public void Remove(InputIssue issue)
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            EndJobViewLocked();
            _files.RemoveAll(f => issue.Paths.Any(p => LocalNames.PathComparer.Equals(f, p)));
            _expansionIssues.Remove(issue);
            _issues.Remove(issue);
            _reloadNeeded = true;
        }
        StartReading();
    }

    public void RemoveAll()
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            _files.Clear();
            _folders.Clear();
            _picked.Clear();
            _gameFolders.Clear();
            _rows.Clear();
            _issues.Clear();
            _expansionIssues.Clear();
            _notes.Clear();
            _titleNotes.Clear();
            _jobs.Clear();
            _jobKeys.Clear();
            _jobShown = false;
            _checkedBefore = [];
            _summary = null;
            _lastOutput = null;
            _bestFirst = false;
            _set?.Dispose();
            _set = null;
        }
        Notify();
    }

    /// <summary>Ticks or unticks a title for the jobs to come.</summary>
    public void Toggle(TitleRow row)
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            int index = _rows.FindIndex(r => r.Key == row.Key);
            if (index >= 0)
                _rows[index] = _rows[index] with { Selected = !_rows[index].Selected, ChosenByHand = true };
        }
        Notify();
    }

    /// <summary>
    /// With compression off, a title only comes out any smaller by having an update made part of it, so there's nothing
    /// in it for these. They may well be ticked for something else, so nothing happens to them without asking. A compressed one
    /// doesn't come out the same size but bigger, uncompressed, and is only ticked by someone who wants that.
    /// </summary>
    public string[] TickedWithoutUpdate()
    {
        if (Settings.Get("--nocompress") != "1")
            return [];
        lock (_gate)
            return _rows.Where(r => r.Selected && SameSizeRepacked(r) && !r.IsDone(BatchMode.Convert)).Select(r => r.Name).ToArray();
    }

    public void UntickWithoutUpdate()
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            _rows = _rows.Select(r => r.Selected && SameSizeRepacked(r) ? r with { Selected = false, ChosenByHand = true } : r).ToList();
        }
        Notify();
    }

    private static bool SameSizeRepacked(TitleRow row) =>
        !row.HasUpdate && !row.IsFolder && !row.IsCompressed && (row.Estimate is not { } estimate || !Grows(estimate));

    /// <summary>Comes out bigger, as a compressed game does with compression off.</summary>
    private static bool Grows(SizeEstimate estimate) => estimate.SavedFraction < -SavingRule.NothingToGain;

    /// <summary>Ticks or unticks everything. Anything the minimum saving left out counts as chosen once it's ticked here.</summary>
    public void SelectAll(bool selected)
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            _rows = _rows.Select(r => r with { Selected = selected, ChosenByHand = true }).ToList();
        }
        Notify();
    }

    /// <summary>
    /// Call when an option changed. Finished titles can then be run again with the new option, and if it decides the
    /// size, the old estimates don't stay on show.
    /// </summary>
    public void OptionChanged(string flag)
    {
        // The parts of a file name only change what new files are called: what's done stays done, as on the Switch.
        if (flag is "--noautoupdate" or ProblemReports.Flag or "--noidinname" or "--noversioninname" or "--nodlccountinname" or "--nolz4inname")
            return;
        if (flag == "--showunusable")
        {
            Notify();
            return;
        }
        if (flag == "--hidesmall")
        {
            lock (_gate)
                _reloadNeeded = true;
            StartReading();
            return;
        }

        // Game and DLC in one file decides which copy of a game is used and what's done, which reading the files again works out, and what's
        // made of a game, which its estimate goes by. The level and block size decide which compressed copies have nothing to gain.
        bool rereads = flag is "--separatedlc" or "--level" or "--blocksize";
        if (rereads)
        {
            lock (_gate)
                _reloadNeeded = true;
        }
        bool decidesSize = flag is "--nocompress" or "--level" or "--blocksize" or "--separatedlc";
        bool decidesSelection = flag is "--minsaving";
        lock (_gate)
        {
            if (!decidesSelection)
            {
                // What a job came to doesn't hold with the new option, so its view goes too.
                EndJobViewLocked();
                _rows = _rows.Select(r => r with
                {
                    Estimate = decidesSize ? null : r.Estimate,
                    State = r.State == RowState.Done ? RowState.Ready : r.State,
                    OutputPath = r.State == RowState.Done ? null : r.OutputPath,
                }).ToList();
                _summary = null;
            }
            if (TryReadOptions() is { } options)
                _rows = _rows.Select(r => Judged(r, options)).ToList();
        }
        Notify();
        if (rereads)
            StartReading();
    }

    private Options? TryReadOptions()
    {
        try
        {
            return Settings.ToOptions(OptionUse.Convert);
        }
        catch (ToolException)
        {
            return null;
        }
    }

    /// <summary>Holds a title against the minimum saving, or with compression off against there being anything for a repack to gain, leaving
    /// alone anything someone has chosen by hand.</summary>
    private TitleRow Judged(TitleRow row, Options options)
    {
        // What a first look can't vouch for isn't left out on its say, but it isn't ticked either until it's been read in full (SavingRule.NotSure).
        bool notSure = row.Estimate is { } first && SavingRule.NotSure(first, options) && !row.DlcWithoutFile;
        string? reason = !options.Compress && row.Estimate is { } grown && Grows(grown) ? $"would grow by {-grown.SavedFraction * 100:0}%, as compression is off"
            : !options.Compress && NothingToGainRepacked(row) && !PickedOnItsOwnLocked(row) ? "nothing to gain: no update to make part of it"
            : row.Estimate is not { } estimate || notSure || row.DlcWithoutFile ? null
            : !SavingRule.WorthCompressing(estimate, options) ? (SavingRule.UnderMinimum(estimate, options) ? "not worth it: " : "")
                // Made again only to put its DLC in its file, it saves nothing, as on the Switch.
                + SavingRule.Reason(row.DlcNotInFile ? estimate with { AlreadyCompressed = false } : estimate, options) : null;
        // One its developer or another tool compressed, which its headers say, is listed with what it saves, and ticked only by hand, as its
        // updates would then need the original game: before it's analyzed too.
        bool theirs = options.Compress && row.ByMakers;
        string again = !options.Compress || reason is not null ? ""
            : theirs ? TitleRow.UpdatesNeedOriginal : row.Estimate is { WasCompressed: true, AlreadyCompressed: false } && !notSure ? TitleRow.SmallerAgain : "";
        return row with
        {
            BelowMinimum = reason, NotSure = notSure, AgainNote = again,
            Selected = row.ChosenByHand ? row.Selected : reason is null && !notSure && !theirs,
        };
    }

    /// <summary>The titles that gain the most for the time they take first, by what each is sure to save, as shown, then those that are measured
    /// first, then those left out, then any with no estimate yet.</summary>
    private static List<TitleRow> BestFirst(IEnumerable<TitleRow> rows) => rows
        .OrderBy(r => r.IsDone(BatchMode.Convert) ? 1 : 0)
        .ThenBy(r => r.Estimate is null ? 3 : r.BelowMinimum is not null ? 2 : r.NotSure ? 1 : 0)
        .ThenByDescending(r => r.Estimate?.SureSavedFraction ?? 0)
        .ThenByDescending(r => r.Estimate?.SureSavedBytes ?? 0)
        .ToList();

    /// <summary>Keys may have changed, so read everything again.</summary>
    public void ReloadKeys()
    {
        lock (_gate)
        {
            _keyset = null;
            _reloadNeeded = true;
        }
        StartReading();
    }

    /// <summary>False if nothing was started, because every title already has an answer or something else is running. A thorough analysis
    /// goes on to read every game in full, behind the list, which anything asked for stops.</summary>
    public bool Analyze()
    {
        var rows = CurrentRows().Where(r => r.ToAnalyze).ToList();
        if (rows.Count == 0 && !CanMeasure)
            return false;
        return Begin(SessionActivity.Analyzing, () =>
        {
            var options = Settings.ToOptions(OptionUse.Convert);
            ForEachRow(rows, (jobs, row) =>
            {
                var estimate = EstimateRow(jobs, row, options);
                return r => Judged(r with { Estimate = estimate, State = row.State == RowState.Failed ? RowState.Ready : row.State }, options);
            });
            lock (_gate)
            {
                _bestFirst = true;
                _rows = BestFirst(_rows);
            }
            // Thorough, every game is read in full straight after, writing nothing, the ones a first look couldn't vouch for first, so every
            // figure is exact. It goes on behind the list, and gives way to anything asked for, keeping what it measured.
            if (options.Measure && options.Compress && ToMeasure() is { Count: > 0 } toMeasure)
            {
                lock (_gate)
                    _activity = SessionActivity.Measuring;
                Notify();
                MeasureRows(toMeasure, options);
            }
        });
    }

    /// <summary>What the ticked games save worked out afresh, as the Switch's "Analyze the ticked games again" does: what was known about them
    /// is forgotten, and a thorough analysis reads them in full again too. False if none is ticked, or something else is running.</summary>
    public bool AnalyzeAgain()
    {
        GiveWay();
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle || !_rows.Any(r => r.Selected && !r.IsDone(BatchMode.Convert)))
                return false;
            _rows = _rows.Select(r => r.Selected && !r.IsDone(BatchMode.Convert)
                ? r with
                {
                    Estimate = null, BelowMinimum = null, NotSure = false, AgainNote = "",
                    State = r.State == RowState.Failed ? RowState.Ready : r.State, Failure = r.State == RowState.Failed ? null : r.Failure,
                }
                : r).ToList();
        }
        Notify();
        return Analyze();
    }

    /// <summary>Whether a thorough analysis has games left to read in full, which Analyze goes on with.</summary>
    public bool CanMeasure => TryReadOptions() is { Measure: true, Compress: true } && ToMeasure().Count > 0;

    /// <summary>The ticked games, or the one game with this key, as its own page and its menu ask for, whether it's ticked or not.</summary>
    private List<TitleRow> RowsFor(string? only, Func<TitleRow, bool> fits) =>
        CurrentRows().Where(r => (only is null ? r.Selected : r.Key == only) && fits(r)).ToList();

    /// <param name="only">The one game to compress, as Compress this game asks: it's done as if ticked by hand.</param>
    public bool Compress(string? only = null)
    {
        // Only what's ticked: a game a first look can't vouch for is ticked once a thorough analysis finds it worth it.
        var rows = RowsFor(only, r => !r.IsDone(BatchMode.Convert));
        if (rows.Count == 0)
            return false;
        return Begin(SessionActivity.Compressing, () => ShowJobLocked(SessionActivity.Compressing, rows), () =>
        {
            // A file where NSPs go that another program has open may be a copy of a game here, which this would then make again beside it. Once
            // it's closed, the list is read again with it before anything's made, so pressing Compress again is all it takes.
            List<string> wasHeld;
            lock (_gate)
                wasHeld = _heldCopies;
            string? held = wasHeld.FirstOrDefault(IsHeld);
            if (held is null && wasHeld.Count > 0)
            {
                ReadInputs();
                lock (_gate)
                {
                    held = _heldCopies.FirstOrDefault();
                    rows = rows.Select(r => _rows.FirstOrDefault(now => now.Key == r.Key)).OfType<TitleRow>().ToList();
                }
            }
            if (held is not null)
                throw new ToolException($"'{Path.GetFileName(held)}' is open in another program. Close it, then try again.");
            var options = Settings.ToOptions(OptionUse.Convert);
            var converter = new TitleConverter(options, RequireKeyset());
            ForEachRow(rows, (jobs, row) =>
            {
                // What's already compressed never needs doing again, whoever ticked it, and is left out before anything reads it. One its
                // developer or another tool compressed, which its headers say, is only done ticked by hand, as its updates would then need the
                // original game. A title nobody has analyzed yet gets its answer now, so the time isn't spent on one that gains too little. One a
                // first look can't vouch for is read in full first, writing nothing, and done only if that finds it worth it. One ticked by hand
                // is done whatever it saves. One compressed before, with nothing new to make of it, is judged whatever the minimum, as made again
                // it can save nothing at all: a copy made again to hold its DLC is made anyway, as on the command line.
                bool byHand = row.ChosenByHand || only is not null;
                string? asItIs = !options.Compress ? null
                    : row.IsCompressed ? jobs.Any(Analyzer.WasCompressed) ? "it's already compressed" : "nothing to gain: it's already as small as it gets"
                    : row.ByMakers && !byHand ? TitleRow.UpdatesNeedOriginal : null;
                if (asItIs is not null)
                {
                    _leftOut++;
                    Ui.Warn($"Left out: {asItIs}");
                    return r => Judged(r with { State = RowState.Ready, Failure = null }, options);
                }
                bool compressedBefore = jobs.Any(Analyzer.CompressedAsItIs) && !jobs.Any(j => j.DlcNotInFile);
                bool judging = options.Compress && !byHand && !row.DlcWithoutFile && (SavingRule.Applies(options) || row.Estimate is not null || compressedBefore);
                if (judging)
                {
                    var estimate = row.Estimate ?? EstimateRow(jobs, row, options);
                    if (!row.IsFolder && SavingRule.NotSure(estimate, options))
                        estimate = MeasureRow(jobs, row, options);
                    if (!SavingRule.WorthCompressing(estimate, options))
                    {
                        _leftOut++;
                        Ui.Warn($"Left out: {SavingRule.Reason(estimate, options)}");
                        return r => Judged(r with { Estimate = estimate, State = RowState.Ready, Failure = null }, options);
                    }
                }

                string? path = null;
                long bytes = 0;
                if (row.IsFolder)
                {
                    var builder = new Builder(FolderOptions(row), RequireKeyset());
                    builder.Run();
                    path = builder.OutputPath!;
                    bytes = new FileInfo(path).Length;
                }
                // The game first, as on the command line. Once its file is made, all its DLC goes into it, one this tool compressed before too.
                // With --separatedlc each DLC gets a file of its own instead, apart from one this tool compressed before, which is one already.
                // DLC without their game go together into one file. Otherwise a game stays as it is when its mark says making it again gains
                // nothing, and so does a DLC with all of it compressed in the file of a game that stays, or held with the other DLC, apart from a
                // DLC with no file of its own beside this tool's copy of its game, which gets one.
                bool gameMade = false, gameKept = false;
                for (int at = 0; at < jobs.Count; at++)
                {
                    var job = jobs[at];
                    bool needsFile = row.DlcWithoutFile && _set!.HasNoFileOfItsOwn(job);
                    if (options.Compress && Analyzer.StaysAsItIs(job, options, row.DlcWithoutFile, needsFile, gameMade, gameKept, _set!))
                    {
                        Ui.Info($"{job.DisplayName}: already compressed");
                        bytes += job.InputBytes;
                        gameKept |= job.IsBaseGame;
                        continue;
                    }
                    Ui.Heading(job.Describe());
                    // The game, then its DLC in title ID order: the DLC still to come go in order after this one.
                    var dlc = !options.SeparateDlc && (job.IsBaseGame || !jobs[0].IsBaseGame) ? jobs.Skip(at + 1).Where(j => !j.IsBaseGame).ToList() : [];
                    var (made, size) = converter.Run(job, _set!, dlc);
                    path ??= made;
                    bytes += size;
                    gameMade |= job.IsBaseGame;
                    if (dlc.Count > 0)
                        break;
                }
                lock (_gate)
                    _lastOutput = path;
                return r => r with { State = RowState.Done, DoneAs = BatchMode.Convert, OutputPath = path, OutputBytes = bytes };
            }, summarize: true);

            if (options.Compress && rows.Any(r => !r.HasUpdate && !r.IsFolder && !r.IsCompressed))
                Ui.Info(TitleJob.UpdatesWontWork);

            // What's been done goes out of the way of what's still to do.
            lock (_gate)
                _rows = _bestFirst ? BestFirst(_rows) : _rows.OrderBy(r => r.IsDone(BatchMode.Convert) ? 1 : 0).ToList();
        });
    }

    /// <summary>What a thorough analysis reads in full, in the order it does: the games a first look can't vouch for first, the likeliest to be
    /// worth it first, then the rest, the most they're sure to save first. Not what's compressed already, which is a fact about the file. Growing
    /// on a first look's say is no fact (SizeEstimate.SureToGrow), so those are read too.</summary>
    private List<TitleRow> ToMeasure() => CurrentRows()
        .Where(r => r.Estimate is { Measured: false, AlreadyCompressed: false } && r.State == RowState.Ready
            && !r.IsDone(BatchMode.Convert) && !r.IsFolder)
        .OrderBy(r => r.NotSure ? 0 : 1)
        .ThenByDescending(r => r.NotSure ? r.Estimate!.SavedFraction : 0)
        .ThenByDescending(r => r.NotSure ? 0 : r.Estimate!.SureSavedBytes)
        .ToList();

    /// <summary>Measures these, saying what came of it, for a thorough analysis.</summary>
    private void MeasureRows(IReadOnlyList<TitleRow> rows, Options options)
    {
        ForEachRow(rows, (jobs, row) =>
        {
            var estimate = MeasureRow(jobs, row, options);
            Ui.Info(Analyzer.MeasuredLine(estimate));
            return r => Judged(r with { Estimate = estimate, State = RowState.Ready, Failure = null }, options);
        }, summarize: true);
        lock (_gate)
            _rows = _bestFirst ? BestFirst(_rows) : _rows;
    }

    /// <param name="only">The one game to extract, as Extract this game asks.</param>
    public bool Extract(string? only = null)
    {
        var rows = RowsFor(only, r => !r.IsFolder && !r.IsDone(BatchMode.Extract));
        if (rows.Count == 0)
            return false;
        return Begin(SessionActivity.Extracting, () => ShowJobLocked(SessionActivity.Extracting, rows), () =>
        {
            var extractor = new TitleExtractor(Settings.ToOptions(OptionUse.Extract));
            ForEachRow(rows, (jobs, row) =>
            {
                string? path = null;
                long bytes = 0;
                foreach (var job in jobs)
                {
                    Ui.Heading(job.Describe());
                    var (made, size) = extractor.Run(job, _set!);
                    path ??= made;
                    bytes += size;
                }
                lock (_gate)
                    _lastOutput = path;
                return r => r with { State = RowState.Done, DoneAs = BatchMode.Extract, OutputPath = path, OutputBytes = bytes };
            }, summarize: true);
        });
    }

    /// <summary>Reads all of each ticked game back, or the one given, to see it's whole, writing nothing (<see cref="GameCheck"/>). What each
    /// game was before comes back once the list is gone back to.</summary>
    /// <param name="only">The one game to check, as Check this game for damage asks.</param>
    public bool Check(string? only = null)
    {
        var rows = RowsFor(only, r => !r.IsFolder);
        if (rows.Count == 0)
            return false;
        return Begin(SessionActivity.Verifying, () =>
        {
            _checkedBefore = rows.ToDictionary(r => r.Key);
            ShowJobLocked(SessionActivity.Verifying, rows);
        }, () =>
        {
            var options = Settings.ToOptions(OptionUse.Convert);
            string? spool = options.TempDir ?? (options.NspDirSet ? options.NspDir : null);
            ForEachRow(rows, (jobs, row) =>
            {
                GameCheck.Run(jobs, spool);
                Ui.Success($"{row.Name}: it's whole");
                return r => r with { State = RowState.Done, DoneAs = BatchMode.Check, Failure = null };
            }, summarize: true);
        });
    }

    /// <summary>A game with its DLC comes to what they come to together, on the console: an NSZ against what it takes installed, as the row is.</summary>
    private SizeEstimate EstimateRow(List<TitleJob> jobs, TitleRow row, Options options)
    {
        if (row.IsFolder)
            return Analyzer.EstimateBuild(FolderOptions(row));
        return SizeEstimate.Together(jobs.Select(job => Analyzer.Estimate(job, options, Analyzer.KnownAsItIs(job, jobs, _set!, options, row.DlcWithoutFile))).ToList()).AsInstalled();
    }

    /// <summary>The same from reading all of it: exactly, for a game under 1 GB, as its files come, the same as on the Switch.</summary>
    private SizeEstimate MeasureRow(List<TitleJob> jobs, TitleRow row, Options options)
    {
        if (row.IsFolder)
            return Analyzer.EstimateBuild(FolderOptions(row).MeasuringGameOf(row.InputBytes), measure: true);
        var measuring = options.MeasuringGameOf(row.FileBytes);
        return SizeEstimate.Together(jobs.Select(job => Analyzer.Measure(job, measuring, Analyzer.KnownAsItIs(job, jobs, _set!, options, row.DlcWithoutFile))).ToList()).AsInstalled();
    }

    /// <summary>How a game folder is built: with what's chosen for compression and where NSPs go.</summary>
    private Options FolderOptions(TitleRow row)
    {
        GameFolder folder;
        lock (_gate)
            folder = _gameFolders[row.Key];
        return folder.BuildOptions(Settings.ToOptions(OptionUse.Build));
    }

    /// <summary>Shares every game on the list with a Switch until it's stopped, hidden ones too, as what to install is chosen on the Switch. Ticks
    /// are for what the PC does.</summary>
    public bool SendToSwitch() => Begin(SessionActivity.Sending, () =>
    {
        string[] files;
        CancellationToken token;
        var watch = new SendWatch();
        watch.Changed += Notify;
        lock (_gate)
        {
            files = _rows.SelectMany(r => r.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            token = _stop!.Token;
            _sendWatch = watch;
        }
        if (files.Length == 0)
            throw new ToolException("Add the game files to send first.");
        Sender.Run(files, token, watch, RequireKeyset);
    });

    public void Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
        }
    }

    /// <summary>Stops whatever is running and waits for it, so a partly written file gets cleaned up before the program ends.</summary>
    public void Dispose()
    {
        Stop();
        _worker?.Join();
        lock (_gate)
        {
            _set?.Dispose();
            _set = null;
        }
    }

    public void Message(UiLevel level, string text)
    {
        lock (_gate)
        {
            if (text.Length == 0)
                return;
            if (_log.Count >= MaxLogLines)
                _log.RemoveRange(0, MaxLogLines / 4);
            _log.Add(new LogLine(level, text));
        }
        Notify();
    }

    public void Progress(ProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            _progress = snapshot;
            // A part that starts again from nothing is another part of the game, as its DLC is.
            if (IsMainStep(snapshot.Label))
            {
                _rowWork += snapshot.Label == _workLabel && snapshot.Done >= _workLastDone ? snapshot.Done - _workLastDone : snapshot.Done;
                _workLastDone = snapshot.Done;
            }
            _workLabel = snapshot.Label;
        }
        Notify();
    }

    /// <summary>Compressing, writing or extracting, which is most of the time a game takes, or reading it through for a check, which is all of
    /// it. Reading what's written to check it is the rest.</summary>
    private static bool IsMainStep(string label) =>
        label.StartsWith("Compressing", StringComparison.Ordinal) || label.StartsWith("Writing", StringComparison.Ordinal) || label.StartsWith("Extracting", StringComparison.Ordinal)
        || label == GameCheck.Reading || label.StartsWith("Hashing", StringComparison.Ordinal);

    public void ProgressDone(string label, TimeSpan elapsed)
    {
        lock (_gate)
        {
            // What came after the last time it said how far it was.
            if (_progress is { } last && last.Label == label && label == _workLabel && IsMainStep(label) && last.Total > _workLastDone)
            {
                _rowWork += last.Total - _workLastDone;
                _workLastDone = last.Total;
            }
            _progress = null;
            if (elapsed.TotalSeconds >= 1)
                _log.Add(new LogLine(UiLevel.Detail, $"{label}: done in {elapsed.TotalSeconds:0.0}s"));
        }
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    /// <summary>The games of the view the list is on, which is what the buttons work on, even from what a job came to.</summary>
    private List<TitleRow> CurrentRows()
    {
        lock (_gate)
            return ListRowsLocked();
    }

    private void StartReading()
    {
        bool needed;
        lock (_gate)
            needed = _queued.Count > 0 || _reloadNeeded;
        if (needed)
            Begin(SessionActivity.Reading, ReadInputs);
    }

    private bool Begin(SessionActivity activity, Action work) => Begin(activity, null, work);

    /// <summary>Starts it if nothing else is going on. Anything but reading the list again is the end of the last job's view, and
    /// <paramref name="starting"/>, if there is one, sets up what this one shows.</summary>
    private bool Begin(SessionActivity activity, Action? starting, Action work)
    {
        // Measuring goes on behind the list, and gives way to anything asked for, keeping what it measured.
        if (activity != SessionActivity.Measuring)
            GiveWay();
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return false;
            if (activity != SessionActivity.Reading)
                LeaveJobViewLocked();
            starting?.Invoke();
            _activity = activity;
            _progress = null;
            _currentTitle = null;
            _currentIndex = _currentCount = 0;
            if (activity != SessionActivity.Reading)
            {
                _summary = null;
                _lastOutput = null;
                _log.Clear();
            }
            _leftOut = 0;
            if (activity != SessionActivity.Reading)
                _sendWatch = null;
            _stop = new CancellationTokenSource();
            Ui.Token = _stop.Token;
        }
        Notify();

        _worker = new Thread(() => RunWorker(work)) { IsBackground = true, Name = "Job" };
        _worker.Start();
        return true;
    }

    /// <summary>Stops measuring, if that's what's going on, and waits for it to have stopped.</summary>
    private void GiveWay()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_activity != SessionActivity.Measuring)
                return;
            _stop?.Cancel();
            worker = _worker;
        }
        worker?.Join();
    }

    private void RunWorker(Action work)
    {
        try
        {
            work();
        }
        catch (OperationCanceledException)
        {
            Finish(StoppedMessage(), UiLevel.Warning);
        }
        catch (AggregateException e) when (e.Flatten().InnerExceptions.All(i => i is OperationCanceledException))
        {
            Finish(StoppedMessage(), UiLevel.Warning);
        }
        catch (Exception e)
        {
            // Whatever went wrong, the window stays open and says so.
            Finish(e.Message, UiLevel.Error);
        }

        lock (_gate)
        {
            if (_activity == _jobKind)
                _jobClock.Stop();
            _activity = SessionActivity.Idle;
            _progress = null;
            _currentTitle = null;
            _rows = _rows.Select(r => r.State == RowState.Working ? r with { State = RowState.Ready } : r).ToList();
        }
        Notify();
        StartReading();
    }

    /// <summary>What came of it, gains first: "2 games compressed in 4 min, 3.20 GB saved".</summary>
    private string Summary(IReadOnlyList<TitleRow> rows, TimeSpan elapsed, int failures)
    {
        var keys = rows.Select(r => r.Key).ToHashSet();
        lock (_gate)
        {
            if (_activity == SessionActivity.Measuring)
            {
                // What it found, and that the ones worth it are ticked now.
                var measured = _rows.Where(r => keys.Contains(r.Key) && r.Estimate is { Measured: true }).ToList();
                var worth = measured.Where(r => r.BelowMinimum is null && !r.NotSure && r.Selected).ToList();
                var found = new List<string> { $"{measured.Count} {(measured.Count == 1 ? "game" : "games")} measured in {ProgressReporter.FormatElapsed(elapsed)}" };
                found.Add(worth.Count == 0 ? "none worth compressing"
                    : $"{worth.Count} worth compressing, saving at least {ProgressReporter.Format(worth.Sum(r => r.Estimate!.SureSavedBytes))}");
                if (failures > 0)
                    found.Add($"{failures} couldn't be read");
                return string.Join(", ", found) + ".";
            }
        }
        bool checking;
        lock (_gate)
            checking = _activity == SessionActivity.Verifying;
        if (checking)
        {
            string elapsedText = ProgressReporter.FormatElapsed(elapsed);
            return failures == 0 ? $"Every game read back as it should, in {elapsedText}."
                : failures == 1 ? $"One game didn't read back as it should, in {elapsedText}."
                : $"{failures} games didn't read back as they should, in {elapsedText}.";
        }
        int done;
        long saved;
        string verb;
        lock (_gate)
        {
            var finished = _rows.Where(r => keys.Contains(r.Key) && r.State == RowState.Done).ToList();
            done = finished.Count;
            bool extracting = _activity == SessionActivity.Extracting;
            saved = extracting ? 0 : finished.Where(r => r.DoneAs == BatchMode.Convert).Sum(r => r.InputBytes - r.OutputBytes);
            verb = extracting ? "extracted" : Settings.Get("--nocompress") == "1" ? "repacked" : "compressed";
        }
        var parts = new List<string>();
        if (done > 0)
            parts.Add($"{done} {(done == 1 ? "game" : "games")} {verb} in {ProgressReporter.FormatElapsed(elapsed)}");
        if (saved > 0)
            parts.Add($"{ProgressReporter.Format(saved)} saved");
        if (failures > 0)
            parts.Add($"{failures} couldn't be done");
        if (_leftOut > 0)
            parts.Add($"{_leftOut} left out as not worth it");
        return parts.Count == 0 ? "Nothing was done." : string.Join(", ", parts) + ".";
    }

    private string StoppedMessage()
    {
        lock (_gate)
        {
            return _activity switch
            {
                SessionActivity.Extracting => "Stopped. Files extracted so far were kept.",
                SessionActivity.Sending => "Stopped sending to the Switch.",
                SessionActivity.Compressing => "Stopped. Nothing was left half written.",
                SessionActivity.Measuring => "Stopped. What's measured so far is kept.",
                SessionActivity.Verifying => "Stopped. Checking changes nothing.",
                _ => "Stopped.",
            };
        }
    }

    private void Finish(string summary, UiLevel level)
    {
        lock (_gate)
        {
            _summary = summary;
            _summaryLevel = level;
        }
    }

    /// <param name="jobs">The game and its DLC, or nothing for a game folder.</param>
    private delegate Func<TitleRow, TitleRow> RowWork(List<TitleJob> jobs, TitleRow row);

    private void ForEachRow(IReadOnlyList<TitleRow> rows, RowWork work, bool summarize = false)
    {
        var clock = Stopwatch.StartNew();
        int failures = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            Ui.Token.ThrowIfCancellationRequested();
            var row = rows[i];
            List<TitleJob> jobs;
            lock (_gate)
            {
                _currentIndex = i + 1;
                _currentCount = rows.Count;
                _currentTitle = row.Name;
                jobs = _jobs.GetValueOrDefault(row.Key) ?? [];
                _rowBytes = row.InputBytes;
                _rowWork = 0;
                _workLabel = null;
            }
            Update(row.Key, r => r with { State = RowState.Working, Failure = null });
            if (row.IsFolder || jobs.Count != 1)
                Ui.Heading($"{row.Name}: {row.Detail}");
            try
            {
                Update(row.Key, work(jobs, row));
            }
            catch (Exception e) when (!Failures.IsStop(e))
            {
                // Whatever it was, it's this game that couldn't be done, and the rest still get their turn.
                failures++;
                string message = Failures.Describe(e);
                Ui.Error($"FAILED: {message}");
                Update(row.Key, r => r with { State = RowState.Failed, Failure = message });
                ProblemReports.JobFailed(message);
            }
            lock (_gate)
            {
                _jobFinishedBytes += row.InputBytes;
                _rowWork = 0;
            }
        }

        if (summarize)
        {
            Finish(Summary(rows, clock.Elapsed, failures), failures == 0 ? UiLevel.Success : UiLevel.Error);
        }
    }

    private void Update(string key, Func<TitleRow, TitleRow> change)
    {
        lock (_gate)
        {
            int index = _rows.FindIndex(r => r.Key == key);
            if (index >= 0)
                _rows[index] = change(_rows[index]);
        }
        Notify();
    }

    /// <summary>The keys, loaded the first time they're needed, for whatever opens a game besides the jobs, as Look inside does.</summary>
    public Keyset Keys() => RequireKeyset();

    /// <summary>Takes the ticked games off the list, as Remove them from the list does.</summary>
    public void RemoveTicked()
    {
        lock (_gate)
        {
            if (_activity != SessionActivity.Idle)
                return;
            EndJobViewLocked();
            var ticked = _rows.Where(r => r.Selected).ToList();
            foreach (var row in ticked)
            {
                _files.RemoveAll(f => row.Files.Contains(f, LocalNames.PathComparer));
                _folders.RemoveAll(f => row.Files.Contains(f, LocalNames.PathComparer));
            }
            _rows.RemoveAll(r => r.Selected);
            _reloadNeeded = true;
        }
        StartReading();
    }

    /// <summary>What asking to rename says, in every version: "Rename this file?" or "Rename 3 files?".</summary>
    public static string RenameQuestion(int count) => count == 1 ? "Rename this file?" : $"Rename {count} files?";

    /// <summary>The choice that leaves them as they are, which comes first.</summary>
    public static string KeepNames(int count) => count == 1 ? "Keep the name" : "Keep the names";

    /// <summary>What Rename the file, for the game with this key, or Rename ticked, for the ticked games of the view, comes to: each one whose file
    /// is this tool's copy, by what's in it, and isn't called what File names calls it, with the name it gets in its own folder. A name another
    /// file has gets " (2)" and on, as a saved file does, and one called so already, " (2)" and all, keeps its name. Nothing while a job runs.</summary>
    public IReadOnlyList<(string From, string To)> RenamePlan(string? only = null)
    {
        lock (_gate)
            return RenamesLocked(only).Select(r => (r.From, r.To)).ToList();
    }

    private List<(string Key, string From, string To)> RenamesLocked(string? only)
    {
        var renames = new List<(string Key, string From, string To)>();
        if (_set is null || _activity is not (SessionActivity.Idle or SessionActivity.Measuring))
            return renames;
        var parts = TryReadOptions()?.Name ?? new NameParts(true, true, true, true);
        var taken = new HashSet<string>(LocalNames.PathComparer);
        foreach (var row in only is null ? ListRowsLocked().Where(r => r.Selected) : _rows.Where(r => r.Key == only))
        {
            // File names gives NSPs' names, so an XCI, NSZ or XCZ keeps its own, as renamed it would say it's something it isn't. Windows takes a
            // name like Con.nsp for a device, and ".nsp" is no name at all: those keep the name they have too.
            if (_jobs.GetValueOrDefault(row.Key) is not [var game, ..] || !game.Main.Archive.FilePath.EndsWith(".nsp", StringComparison.OrdinalIgnoreCase)
                || _set.CopyName(game, parts) is not { } name || LocalNames.ForFileSystem(name) != name || name == ".nsp")
                continue;
            string from = game.Main.Archive.FilePath;
            string to;
            try
            {
                to = LocalNames.FreePath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(from))!, name), from, taken);
            }
            catch (ToolException)
            {
                continue;
            }
            if (LocalNames.PathComparer.Equals(to, Path.GetFullPath(from)))
                continue;
            taken.Add(to);
            renames.Add((row.Key, from, to));
        }
        return renames;
    }

    /// <summary>Carries out <see cref="RenamePlan"/> a file at a time, never over a file that's there. The list follows the new names, ticks and
    /// all, and is read again. A file that can't be renamed keeps its name, and its row says why. False if there was nothing to rename, or
    /// something else is going on.</summary>
    public bool Rename(string? only = null)
    {
        GiveWay();
        var said = new List<LogLine>();
        lock (_gate)
        {
            var keys = RenamesLocked(only).Select(r => r.Key).ToList();
            if (_activity != SessionActivity.Idle || keys.Count == 0)
                return false;
            EndJobViewLocked();
            foreach (string key in keys)
            {
                // Worked out again for each, as one renamed before it may have taken the name it would have had.
                if (RenamesLocked(key) is not [var (_, from, to)])
                    continue;
                int index = _rows.FindIndex(r => r.Key == key);
                try
                {
                    File.Move(from, to);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Why, in words the lists turn into a few plain ones (Help.PlainReason), as the system's own message is in its own language.
                    string? why = e is FileNotFoundException or DirectoryNotFoundException ? "it isn't there any more"
                        : e is PathTooLongException ? "its new name is too long"
                        : e is UnauthorizedAccessException ? "changing it isn't allowed"
                        : (e.HResult & 0xFFFF) is 32 or 33 ? "another program has it open"
                        : File.Exists(to) || Directory.Exists(to) ? "another file has that name" : null;
                    string failure = $"'{Path.GetFileName(from)}' couldn't be renamed to '{Path.GetFileName(to)}'{(why is null ? "" : $", as {why}")}: {e.Message}";
                    said.Add(new LogLine(UiLevel.Error, failure));
                    if (index >= 0)
                        _rows[index] = _rows[index] with { State = RowState.Failed, Failure = failure };
                    continue;
                }
                said.Add(new LogLine(UiLevel.Info, $"Renamed '{Path.GetFileName(from)}' to '{Path.GetFileName(to)}'"));
                string Moved(string path) => LocalNames.PathComparer.Equals(path, from) ? to : path;
                for (int i = 0; i < _files.Count; i++)
                    _files[i] = Moved(_files[i]);
                if (_picked.Remove(Path.GetFullPath(from)))
                    _picked.Add(to);
                if (_lastOutput is { } last)
                    _lastOutput = Moved(last);
                if (index >= 0)
                {
                    var row = _rows[index];
                    _rows[index] = row with
                    {
                        Files = row.Files.Select(Moved).ToArray(), OutputPath = row.OutputPath is { } output ? Moved(output) : null,
                        State = row.State == RowState.Failed ? RowState.Ready : row.State, Failure = row.State == RowState.Failed ? null : row.Failure,
                    };
                }
            }
            _log.AddRange(said);
            _reloadNeeded = true;
        }
        Notify();
        StartReading();
        return true;
    }

    private Keyset RequireKeyset()
    {
        lock (_gate)
        {
            if (_keyset is not null)
                return _keyset;
        }

        var keyset = Keyset.Load(Keyset.Find(Settings.ToOptions(OptionUse.Convert).KeysetPath));
        _ = keyset.HeaderKey;
        lock (_gate)
            _keyset = keyset;
        return keyset;
    }

    /// <summary>DLC on its own has no name of its own, so it goes by its file's, without what's in brackets.</summary>
    private static string DlcName(TitleJob job)
    {
        string name = Path.GetFileNameWithoutExtension(job.Main.Archive.FilePath);
        int bracket = name.IndexOf('[');
        name = (bracket > 0 ? name[..bracket] : name).Trim();
        return name.Length > 0 ? $"{name} (DLC)" : "DLC";
    }

    private void ReadInputs()
    {
        List<string> queued;
        var notes = new List<string>();
        var issues = new List<InputIssue>();
        lock (_gate)
        {
            queued = _queued.ToList();
            _queued.Clear();
            _reloadNeeded = false;
        }

        // What it's on, for the window to show it's getting somewhere: going through folders, then opening each file.
        void Reading(string what, int index, int count)
        {
            lock (_gate)
            {
                _currentTitle = what;
                _currentIndex = index;
                _currentCount = count;
            }
            Notify();
        }

        // Being asked to stop goes for all of it, from going through folders to opening each file.
        var token = Ui.Token;
        Reading("Looking through folders", 0, 0);
        var foundFolders = new List<string>();
        var found = TitleSet.Expand(queued, issues, foundFolders, token);
        List<string> files, folders;
        var addedFiles = new List<string>();
        var addedFolders = new List<string>();
        lock (_gate)
        {
            foreach (string folder in foundFolders)
            {
                if (!_folders.Contains(folder, LocalNames.PathComparer))
                {
                    _folders.Add(folder);
                    addedFolders.Add(folder);
                }
            }
            folders = _folders.ToList();
            _notes.AddRange(notes);
            _expansionIssues.AddRange(issues.Where(i => !_expansionIssues.Any(known => known.Path == i.Path)));
            foreach (string file in found)
            {
                if (!_files.Contains(file, LocalNames.PathComparer))
                {
                    _files.Add(file);
                    addedFiles.Add(file);
                }
            }
            files = _files.ToList();
        }
        try
        {
            ReadInto(files, folders, Reading, token);
        }
        catch (Exception e) when (Failures.IsStop(e))
        {
            // Stopped before the list could change, so what was being added isn't on it, and doesn't turn up with whatever's added next.
            lock (_gate)
            {
                _files.RemoveAll(f => addedFiles.Contains(f, LocalNames.PathComparer));
                _folders.RemoveAll(f => addedFolders.Contains(f, LocalNames.PathComparer));
            }
            throw;
        }
    }

    /// <summary>
    /// Files that going through a folder turned up, and that can't be used for the same reason, are one row a folder once there are a few:
    /// a folder of hundreds of them would otherwise bury the games. A file chosen by itself always has a row of its own.
    /// </summary>
    private List<InputIssue> Grouped(IEnumerable<InputIssue> issues)
    {
        const int Together = 3;
        var result = new List<InputIssue>();
        // What's wrong, without the file in front, whether by its name or, as reading it puts it, by its whole path.
        static string? Reason(InputIssue issue)
        {
            string name = $"'{Path.GetFileName(issue.Path)}': ";
            if (!issue.Message.StartsWith(name, StringComparison.Ordinal))
                return null;
            string reason = issue.Message[name.Length..];
            string whole = $"'{issue.Path}' ";
            return reason.StartsWith(whole, StringComparison.OrdinalIgnoreCase) ? reason[whole.Length..] : reason;
        }

        // A reason that goes on from the file, like "is not an NSP", follows its name, and any other follows a colon.
        static string About(string file, string reason) =>
            reason.StartsWith("is ", StringComparison.Ordinal) || reason.StartsWith("has ", StringComparison.Ordinal) ? $"'{file}' {reason}" : $"'{file}': {reason}";

        var groups = issues.GroupBy(i => Reason(i) is { } reason && !_picked.Contains(Path.GetFullPath(i.Path))
            ? Path.GetDirectoryName(Path.GetFullPath(i.Path)) + "|" + reason : "|" + i.Path).ToList();
        static string NameOf(string folder) => Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } last ? last : folder;
        // Folders of the same name in different places are told apart by where they are.
        var folderNames = groups.Where(g => g.Count() >= Together).Select(g => Path.GetDirectoryName(Path.GetFullPath(g.First().Path))!).Distinct().ToList();
        var repeated = folderNames.GroupBy(NameOf, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var items = group.ToList();
            if (Reason(items[0]) is not { } reason)
            {
                result.AddRange(items);
                continue;
            }
            if (items.Count < Together)
            {
                result.AddRange(items.Select(i => i with { Message = About(Path.GetFileName(i.Path), reason) }));
                continue;
            }
            string folder = Path.GetDirectoryName(Path.GetFullPath(items[0].Path))!;
            string name = repeated.Contains(NameOf(folder)) ? folder : NameOf(folder);
            string first = Path.GetFileName(items[0].Path);
            string such = reason.StartsWith("is ", StringComparison.Ordinal) || reason.StartsWith("has ", StringComparison.Ordinal)
                ? $"such as '{first}', which {reason}" : $"such as '{first}': {reason}";
            result.Add(new InputIssue(folder, $"{items.Count} files in '{name}' couldn't be used, {such}") { Paths = items.SelectMany(i => i.Paths).ToArray() });
        }
        // What wasn't added by itself is only listed when that's asked for.
        return result.Select(i => i with { FoundInFolder = i.Paths.All(p => File.Exists(p) && !_picked.Contains(Path.GetFullPath(p))) }).ToList();
    }

    /// <summary>Copies this made of the games in a set, where it puts NSPs: the NSP folder, or beside each game's own files. A file is one
    /// by what's in it, whatever it's called: it holds a title this made, of a game on the list or a DLC of one, or holds only DLC of one. Files that can't be opened
    /// aren't copies.</summary>
    private List<string> CopiesOf(TitleSet set, List<string> files, Keyset keyset, List<string> held)
    {
        string nspDir = Settings.Get("--nspdir");
        // Each file's folder from its full path, as a bare file name has none of its own.
        List<string> folders = nspDir.Length > 0 ? [nspDir]
            : files.Select(f => Path.GetDirectoryName(Path.GetFullPath(f))).OfType<string>().Distinct(LocalNames.PathComparer).ToList();
        var games = set.Jobs.Select(j => j.ApplicationId).ToHashSet();
        var have = new HashSet<string>(files.Select(Path.GetFullPath), LocalNames.PathComparer);
        var copies = new List<string>();
        foreach (string folder in folders)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (!GameArchive.IsGameFile(file) || have.Contains(Path.GetFullPath(file)) || !MadeHereOfAGame(file))
                        continue;
                    have.Add(Path.GetFullPath(file));
                    copies.Add(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A folder that's gone, can't be looked in, or isn't one at all, such as an NSP folder setting that isn't a path, has no copies to find.
            }
        }
        return copies;

        bool MadeHereOfAGame(string file)
        {
            try
            {
                // One another program holds open can't be told from a copy that's there, so it's brought in, to be said as a file that couldn't be opened.
                if (IsHeld(file))
                {
                    held.Add(file);
                    return true;
                }
                using var opened = TitleSet.OpenOnly([file], keyset);
                var titles = opened.Summarize();
                // Its mark counts as well as its compressed data: a game this had nothing to compress in has a mark and no compressed data.
                // A file holding only DLC of a listed game is the DLC's file of its own, whoever made it, as DLC carry no mark.
                return titles.Any(t => (t.MadeHere || t.OriginalBytes > 0) && games.Contains(t.Type == Cnmt.MetaApplication ? t.TitleId : t.LinkedId))
                    || (titles.Count > 0 && titles.All(t => t.Type == Cnmt.MetaAddOnContent && games.Contains(t.LinkedId)));
            }
            catch (Exception e) when (e is ToolException or IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
            {
                // Damaged, or needing keys it hasn't got: it isn't something this made.
                return false;
            }
        }
    }

    /// <summary>Another program has the file open for itself alone, so it can't be read. One that's gone isn't held.</summary>
    private static bool IsHeld(string file)
    {
        try
        {
            using var _ = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Opens every file and game folder, and puts what's in them on the list in place of what was there.</summary>
    private void ReadInto(List<string> files, List<string> folders, Action<string, int, int> reading, CancellationToken token)
    {
        Keyset keyset;
        try
        {
            keyset = RequireKeyset();
            lock (_gate)
                _keyProblem = null;
        }
        catch (Exception e) when (e is ToolException or IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _keyProblem = e.Message;
                _rows.Clear();
                _issues.Clear();
            }
            return;
        }

        // Game and DLC in one file decides which copy of a game is used, so the files are read again when it changes.
        bool together = TryReadOptions() is not { SeparateDlc: true };
        var set = TitleSet.Load(files, keyset, (done, count) =>
        {
            token.ThrowIfCancellationRequested();
            reading("Opening game files", done, count);
        }, token, dlcInGameFile: together);
        // A copy this made where NSPs go belongs with the game it was made from, as one found in the same folder does: otherwise a game compressed
        // into the NSP folder is to do again the next time this opens. Only copies of games on the list come in.
        var held = new List<string>();
        var nspCopies = CopiesOf(set, files, keyset, held);
        lock (_gate)
            _heldCopies = held;
        if (nspCopies is { Count: > 0 } madeHere)
        {
            set.Dispose();
            set = TitleSet.Load([.. files, .. madeHere], keyset, (done, count) =>
            {
                token.ThrowIfCancellationRequested();
                reading("Opening game files", done, count);
            }, token, dlcInGameFile: together);
        }
        // Games under 1 MB, like forwarders, are left off as on the Switch, with the option to list them.
        var current = TryReadOptions();
        if (current is not null)
            set.LeaveOutSmall(current);

        // Game folders are only read again when they're new, as their size means going through every file in them.
        var opened = new Dictionary<string, GameFolder>();
        var folderProblems = new List<InputIssue>();
        foreach (string path in folders)
        {
            string key = "folder|" + Path.GetFullPath(path);
            GameFolder? known;
            lock (_gate)
                known = _gameFolders.GetValueOrDefault(key);
            if (known is null)
            {
                if (token.IsCancellationRequested)
                {
                    set.Dispose();
                    token.ThrowIfCancellationRequested();
                }
                reading("Reading " + Path.GetFileName(path), opened.Count + 1, folders.Count);
            }
            try
            {
                opened[key] = known ?? GameFolder.Open(path);
            }
            catch (Exception e) when (e is ToolException or IOException or UnauthorizedAccessException)
            {
                folderProblems.Add(new InputIssue(path, $"'{Path.GetFileName(path)}': {Help.WithNextStep(e.Message)}"));
            }
        }

        // What each of this tool's copies freed, worked out the same way analyzing does it. This runs before taking the lock, because a DLC's
        // figure means reading its tables.
        var freedOptions = current?.Clone() ?? new Options();
        var freedOf = set.Jobs.ToDictionary(j => j, j =>
        {
            // A part that can't be read gives nothing here; analyzing it says what's wrong.
            try { return Analyzer.Freed(j, freedOptions); }
            catch (Exception e) when (e is ToolException or IOException) { return 0L; }
        });

        lock (_gate)
        {
            var previous = _rows.ToDictionary(r => r.Key);
            _set?.Dispose();
            _set = set;
            _jobs.Clear();
            _rows = [];
            // Whether a row is compressed goes by its files and the level and block size chosen, so it's worked out with compression on
            // whatever that setting is.
            var compressing = current?.Clone() ?? new Options();
            compressing.Compress = true;
            // A game, its update and its DLC are one row, as they are on the Switch.
            foreach (var game in set.Jobs.GroupBy(j => j.ApplicationId))
            {
                var jobs = game.OrderBy(j => j.IsBaseGame ? 0 : 1).ToList();
                var main = jobs[0];
                int dlc = jobs.Count(j => !j.IsBaseGame);
                string key = $"game|{game.Key:X16}";
                var paths = jobs.SelectMany(j => j.Update is { } update ? new[] { j.Main.Archive.FilePath, update.Archive.FilePath } : [j.Main.Archive.FilePath]).ToArray();
                // It's compressed as compressing would leave all of it as it is: a game whose mark says making it again gains nothing, or with
                // nothing to compress, and its DLC in its file, or DLC without their game held in one file. A DLC with nothing to compress goes
                // into its game's file as it is, so its headers don't say this tool made it. Beside this tool's copy of its game, one with no file
                // of its own, such as one only inside a merged original, is to do until it has one.
                bool gameOurs = main.IsBaseGame && main.IsOwnOutput;
                bool withoutFile = gameOurs && jobs.Any(set.HasNoFileOfItsOwn);
                bool gameKept = main.IsBaseGame && Analyzer.StaysAsItIs(main, compressing, false, false, false, false, set);
                bool compressed = !withoutFile && jobs.All(j => Analyzer.StaysAsItIs(j, compressing, false, false, false, gameKept, set));
                // One its developer or another tool compressed, which its headers say, starts unticked, saying why, while compressing.
                bool byMakers = !compressed && jobs.Any(Analyzer.ByMakers);
                bool theirs = byMakers && CompressingLocked();
                bool picked = paths.Any(f => _picked.Contains(Path.GetFullPath(f)));
                // A copy made since 1.5 says what the game came to before, which gives how much smaller it is; its DLC count what they unpack to.
                long smaller = compressed ? jobs.Sum(j => freedOf[j]) : 0;
                // A copy without DLC of its game that's here is to do: its file is made again with them.
                bool copy = main.IsBaseGame && main.DlcNotInFile;
                string what = !main.IsBaseGame ? "" : compressed || main.IsOwnOutput || copy
                    ? "compressed" + (smaller > 0 ? $", {ProgressReporter.Format(smaller)} smaller" : "") : main.Detail;
                string shortWhat = !main.IsBaseGame ? "" : compressed || main.IsOwnOutput || copy ? "compressed" : main.Update is not null ? "with its update" : main.Detail;
                // An NSZ or XCZ counts at what it takes installed as it is, which any installer unpacks it to, as what's made of it goes on the
                // console compressed as it is. Its files are smaller than that, which the row says.
                long fileBytes = jobs.Sum(j => j.InputBytes);
                bool packed = jobs.Any(Analyzer.IsPacked);
                long counted = packed ? jobs.Sum(j => Analyzer.IsPacked(j) ? CopyMark.InstalledBytes(j) : j.InputBytes) : fileBytes;
                string packedAs = !packed ? "" : $"{ProgressReporter.Format(fileBytes)} packed as {(paths.Any(p => p.EndsWith(".xcz", StringComparison.OrdinalIgnoreCase)) ? "XCZ" : "NSZ")}";
                int apart = copy ? jobs.Count(j => !j.IsBaseGame && !set.InFileOf(main, j)) : 0;
                string dlcs = dlc == 0 ? "" : apart == 0 ? $"{dlc} DLC" : apart == dlc ? $"{dlc} DLC not in its file" : $"{dlc} DLC, {apart} not in its file";
                string detail = string.Join(", ", new[] { what, dlcs, packedAs }.Where(s => s.Length > 0));
                string shortDetail = string.Join(", ", new[] { shortWhat, dlc == 0 ? "" : apart > 0 ? "DLC not in its file" : $"{dlc} DLC" }.Where(s => s.Length > 0));
                var rowNotes = jobs.SelectMany(j => j.IsBaseGame || j.Notes.Count == 0 ? j.Notes : j.Notes.Select(n => $"DLC {j.Main.Meta.TitleId:X16}: {n}")).ToArray();
                var row = new TitleRow(key, main.IsBaseGame ? main.DisplayName : DlcName(main), detail, counted, paths)
                {
                    PackedBytes = packed ? fileBytes : 0,
                    HasUpdate = main.Update is not null,
                    ShortDetail = shortDetail == detail ? "" : shortDetail,
                    Icon = jobs.Select(j => j.Icon).FirstOrDefault(i => i is not null),
                    Notes = rowNotes,
                    AlreadyCompressed = compressed,
                    DlcWithoutFile = withoutFile,
                    DlcNotInFile = copy,
                    Smaller = smaller,
                    ByMakers = byMakers,
                    AgainNote = theirs ? TitleRow.UpdatesNeedOriginal : "",
                    // A compressed game starts unticked, as there's nothing for compressing to do, and so does one its developer compressed,
                    // unless it was chosen on its own: then it's ticked, ready to send to the Switch.
                    Selected = (!compressed && !theirs) || picked,
                    ChosenByHand = (compressed || theirs) && picked,
                };
                // With compression off, one in one piece has nothing for a repack to gain, which is known before it's analyzed.
                if (!CompressingLocked() && NothingToGainRepacked(row) && !PickedOnItsOwnLocked(row))
                    row = row with { Selected = false, BelowMinimum = "nothing to gain: no update to make part of it" };
                if (previous.TryGetValue(key, out var known))
                    row = known with { Name = row.Name, Detail = row.Detail, ShortDetail = row.ShortDetail, InputBytes = row.InputBytes, PackedBytes = row.PackedBytes, Files = paths, Icon = row.Icon, Notes = rowNotes, AlreadyCompressed = compressed, DlcWithoutFile = withoutFile, DlcNotInFile = copy, Smaller = row.Smaller, ByMakers = byMakers };
                _rows.Add(row);
                _jobs[key] = jobs;
            }
            _gameFolders.Clear();
            foreach (var copies in opened.GroupBy(f => f.Value.TitleId))
            {
                var ordered = copies.OrderByDescending(f => f.Value.Version).ThenBy(f => f.Value.Path, StringComparer.OrdinalIgnoreCase).ToList();
                var (key, folder) = ordered[0];
                foreach (var (otherKey, other) in ordered)
                    _gameFolders[otherKey] = other;
                string detail = folder.Version > 0 ? $"game folder v{folder.Version}" : "game folder";
                string[] copyNotes = ordered.Skip(1).Select(o => o.Value.Version == folder.Version
                    ? $"Another copy of this game folder is in '{o.Value.Path}'"
                    : $"An older game folder, v{o.Value.Version}, is in '{o.Value.Path}'").ToArray();
                var row = new TitleRow(key, folder.Name, detail, folder.Bytes, [folder.Path]) { IsFolder = true, Icon = folder.Icon, Notes = copyNotes };
                if (previous.TryGetValue(key, out var known))
                    row = known with { Name = row.Name, Detail = row.Detail, InputBytes = row.InputBytes, Icon = row.Icon, Notes = row.Notes, Smaller = row.Smaller };
                _rows.Add(row);
                _gameFolders[key] = folder;
            }
            if (_bestFirst)
                _rows = BestFirst(_rows);
            _issues = Grouped(set.Problems.Concat(folderProblems));
            _titleNotes = set.Notes.ToList();
            _rows = _rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (_bestFirst)
                _rows = BestFirst(_rows);
        }
    }
}

/// <summary>The paths in text pasted into a console, which is what dropping files on one comes down to.</summary>
internal static class PathList
{
    public static List<string> Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return [];
        if (File.Exists(text) || Directory.Exists(text))
            return [text];

        var paths = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '\\' && !OperatingSystem.IsWindows() && i + 1 < text.Length)
            {
                current.Append(text[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                    paths.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            paths.Add(current.ToString());
        return paths;
    }
}
