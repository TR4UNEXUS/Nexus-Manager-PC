using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Hats.Core;

// The normal UI owns confirmation and logging. Only this short-lived worker is elevated.
[SupportedOSPlatform("windows")]
public static class FormatElevation
{
    const string Prefix = "NexusHATSDesktop.Format.";
    sealed record Reply(string Kind, double Percent = 0, string? Message = null);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    static string PipeName(string token) => Guid.TryParseExact(token, "N", out _)
        ? Prefix + token : throw new IOException("Invalid format request.");

    public static Task RunAsync(SdVolume volume, Action<double> progress) =>
        WindowsVolumes.IsUserAnAdmin()
            ? Task.Run(() => FormatSelected(volume, progress))
            : RunWorkerAsync(volume, progress, LaunchElevated, WindowsVolumes.Validate);

    static Process LaunchElevated(string token, int parentId)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Cannot locate the installer executable."))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--format-helper"); start.ArgumentList.Add(token);
        start.ArgumentList.Add(parentId.ToString(CultureInfo.InvariantCulture));
        try { return Process.Start(start) ?? throw new IOException("Windows did not start the formatter."); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator permission was cancelled.", e); }
    }

    internal static async Task RunWorkerAsync(SdVolume volume, Action<double> progress,
        Func<string, int, Process> launch, Action<SdVolume> validate)
    {
        validate(volume);
        var token = Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(PipeName(token), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        using var worker = launch(token, Environment.ProcessId);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = pipe.WaitForConnectionAsync(connectionTimeout.Token);
        var exited = worker.WaitForExitAsync();
        if (await Task.WhenAny(connected, exited) != connected && !pipe.IsConnected)
            throw new IOException("The formatter stopped before connecting. No format was performed.");
        try { await connected; }
        catch (OperationCanceledException e) { throw new IOException("The formatter did not connect. No format was performed.", e); }
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != worker.Id)
            throw new IOException("Could not verify the formatter process. No format was performed.");
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        validate(volume); // A card can be replaced while the Windows permission prompt is open.
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
            await writer.WriteLineAsync(JsonSerializer.Serialize(volume));
        bool completed = false;
        string? error = null;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var reply = JsonSerializer.Deserialize<Reply>(line) ?? throw new IOException("Invalid formatter response.");
                if (reply.Kind == "progress") progress(reply.Percent);
                else if (reply.Kind == "complete") { completed = true; break; }
                else if (reply.Kind == "error") { error = reply.Message; break; }
                else throw new IOException("Unknown formatter response.");
            }
        }
        finally
        {
            // Release a disconnected progress channel before waiting, so a worker cannot
            // block writing to a pipe the UI has stopped reading. Disk work still finishes.
            pipe.Dispose();
            await exited;
        }
        if (!completed || worker.ExitCode != 0)
            throw new IOException(error ?? "The formatter stopped without confirming completion. Keep the card connected and check the log.");
    }

    public static Task<int> RunHelperAsync(string token, int parentId) =>
        RunHelperAsync(token, parentId, FormatSelected, WindowsVolumes.IsUserAnAdmin);

    internal static async Task<int> RunHelperAsync(string token, int parentId,
        Action<SdVolume, Action<double>> format, Func<bool> isAdministrator)
    {
        try
        {
            if (!isAdministrator()) return 1;
            using var pipe = new NamedPipeClientStream(".", PipeName(token), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId) return 1;
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 1;
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var request = await reader.ReadLineAsync(requestTimeout.Token);
            if (request == null || request.Length > 16384) return 1;
            var volume = JsonSerializer.Deserialize<SdVolume>(request) ?? throw new IOException("No card was selected.");
            try
            {
                format(volume, value =>
                {
                    // Losing the UI must not interrupt a format already writing metadata.
                    try { writer.WriteLine(JsonSerializer.Serialize(new Reply("progress", value))); }
                    catch (IOException) { }
                });
                writer.WriteLine(JsonSerializer.Serialize(new Reply("complete")));
                return 0;
            }
            catch (Exception e)
            {
                try { writer.WriteLine(JsonSerializer.Serialize(new Reply("error", Message: e.ToString()))); }
                catch (IOException) { }
                return 1;
            }
        }
        catch (Exception) { return 1; } // A failed handshake must never reach disk writes.
    }

    static void FormatSelected(SdVolume volume, Action<double> progress)
    {
        WindowsVolumes.Validate(volume);
        using var operation = new Semaphore(1, 1, "Local\\HATS-Volume-" + volume.Identity.Serial.ToString("X8"));
        if (!operation.WaitOne(0)) throw new IOException("Another HATS operation is using this card.");
        try
        {
            WindowsVolumes.Validate(volume);
            progress(0);
            Fat32Formatter.Format(new RawVolume(volume.Root), progress);
        }
        finally { operation.Release(); }
    }
}
