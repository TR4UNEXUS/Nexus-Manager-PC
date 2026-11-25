using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HacBrewPackLz;

internal sealed class ProgressReporter(string label, long total, bool showOutputSize = false, bool showSizes = true)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastReport = -1;

    public void Report(long done, long output)
    {
        Ui.Token.ThrowIfCancellationRequested();

        long now = _clock.ElapsedMilliseconds;
        if (_lastReport >= 0 && now - _lastReport < Ui.Sink.ProgressInterval.TotalMilliseconds)
            return;
        _lastReport = now;
        Ui.Sink.Progress(new ProgressSnapshot(label, done, total, output, showOutputSize, now > 0 ? done / (now / 1000.0) : 0, showSizes));
    }

    public void Done() => Ui.Sink.ProgressDone(label, _clock.Elapsed);

    public static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 90
        ? $"{elapsed.TotalSeconds:0} s"
        : $"{elapsed.TotalMinutes:0.0} min";

    /// <summary>Roughly how long the rest will take.</summary>
    public static string TimeLeft(double seconds)
    {
        if (seconds < 60)
            return "under a minute left";
        if (seconds < 90)
            return "about a minute left";
        long minutes = (long)Math.Round(seconds / 60);
        return minutes < 60 ? $"about {minutes} min left" : $"about {minutes / 60} h {minutes % 60} min left";
    }

    /// <summary>What a step is called on screen: in words anyone knows, rather than the names of the parts of a game file, which the
    /// command line keeps.</summary>
    public static string Plain(string label) => label switch
    {
        "Sampling RomFS" => "Working out what it saves",
        "Checking RomFS" or "Checking section" or "Hashing RomFS" => "Checking the game data",
        "Compressing RomFS" => "Compressing the game data",
        "Comparing RomFS with source" => "Comparing it with the original",
        "Hashing NCA" => "Checking the new file",
        _ when label.StartsWith("Writing ", StringComparison.Ordinal) && label.EndsWith(" NCA", StringComparison.Ordinal) => "Writing the new file",
        _ when label.StartsWith("Extracting ", StringComparison.Ordinal) => "Extracting",
        _ => label,
    };

    /// <summary>Each number joined to the word after it, so a line that wraps never parts "85" from "MB/s".</summary>
    public static string KeepFiguresWhole(string text) => Regex.Replace(text, @"(\d) ", "$1\u00A0");

    /// <summary>Sizes the way Windows Explorer shows them, so the numbers match what people see there.</summary>
    public static string Format(long bytes)
    {
        const double Ki = 1024;
        string sign = bytes < 0 ? "-" : "";
        long value = Math.Abs(bytes);
        return sign + value switch
        {
            >= 1L << 30 => $"{value / (Ki * Ki * Ki):0.00} GB",
            >= 1L << 20 => $"{value / (Ki * Ki):0.0} MB",
            >= 1L << 10 => $"{value / Ki:0.0} KB",
            _ => $"{value} B",
        };
    }
}
