using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NexusFormatter;

public static class DiskBackend
{
    private const string Prefix = "NexusManagerPC.Usb.";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly HashSet<string> Operations = new() { "Format", "InspectPartition", "Convert", "Recover" };
    private sealed record Request(string Mode, JsonElement Data);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static Dictionary<string,object> Event(string line)
    {
        try { using var document = JsonDocument.Parse(line); return Value(document.RootElement) as Dictionary<string,object>; }
        catch (JsonException) { return null; }
    }
    private static object Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Value).ToArray(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    internal static async Task<int> RunAsync(string mode, Dictionary<string,object> data, Action<string> report)
    {
        if (mode != "Inventory" && !Operations.Contains(mode)) throw new ArgumentException("Unknown formatter operation.");
        var request = new Request(mode, JsonSerializer.SerializeToElement(data));
        if (mode == "Inventory" || Elevated())
        {
            var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
            return await Task.Run(() => ExecuteAsync(request, line => dispatcher.Invoke(()=>report(line))));
        }
        var token = Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(Prefix + token, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--usb-operation");
        start.ArgumentList.Add(token);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Process helper;
        try { helper = Process.Start(start) ?? throw new IOException("Windows could not start the disk operation."); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { throw new OperationCanceledException("Windows permission was cancelled."); }
        using (helper)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = helper.WaitForExitAsync();
            if (await Task.WhenAny(connected,exited) == exited && !pipe.IsConnected) throw new IOException("Disk worker did not connect.");
            await connected;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id) throw new IOException("Unexpected disk worker.");
            using var writer = new StreamWriter(pipe, Utf8, 4096, true) {AutoFlush=true};
            using var reader = new StreamReader(pipe, Utf8, false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            string line;
            while ((line = await reader.ReadLineAsync()) != null) report(line);
            await exited;
            return helper.ExitCode;
        }
    }

    public static async Task<int> WorkerAsync(string token, int parentId)
    {
        if (!Guid.TryParseExact(token, "N", out _) || !Elevated()) return 1;
        try
        {
            using var pipe = new NamedPipeClientStream(".", Prefix + token, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(15000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var peer) || peer != parentId) return 1;
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return 1;
            using var reader = new StreamReader(pipe,Utf8,false,4096,true);
            using var writer = new StreamWriter(pipe,Utf8,4096,true){AutoFlush=true};
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line == null || line.Length > 524288) return 1;
            var request = JsonSerializer.Deserialize<Request>(line);
            if (request == null || !Operations.Contains(request.Mode)) return 1;
            void Report(string message) { try { writer.WriteLine(message); } catch(IOException) { } }
            try { return await ExecuteAsync(request, Report); }
            catch(Exception e) { Report(JsonSerializer.Serialize(new {kind="error",message=e.Message})); return 1; }
        }
        catch { return 1; }
    }

    private static async Task<int> ExecuteAsync(Request request, Action<string> report)
    {
        // Elevated PowerShell reads only application resources extracted under an admin-only directory.
        var elevated = Elevated();
        var baseDir = elevated ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp") : Path.GetTempPath();
        var folder = new DirectoryInfo(Path.Combine(baseDir,"NexusUSB-"+Guid.NewGuid().ToString("N")));
        if (elevated)
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true,false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            acl.SetOwner(admins);
            foreach (var principal in new[]{admins,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(principal,FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            folder.Create(acl);
        }
        else folder.Create();
        try
        {
            var assembly = typeof(DiskBackend).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(n=>n.EndsWith(".ps1")||n.EndsWith(".cs")))
            {
                using var source = assembly.GetManifestResourceStream(resource);
                using var target = new FileStream(Path.Combine(folder.FullName,resource),FileMode.CreateNew,FileAccess.Write);
                source.CopyTo(target);
            }
            var requestFile = Path.Combine(folder.FullName,"request.json");
            File.WriteAllText(requestFile,request.Data.GetRawText(),Utf8);
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var psDirectory = Path.Combine(system,@"WindowsPowerShell\v1.0");
            var start = new ProcessStartInfo(Path.Combine(psDirectory,"powershell.exe")) {
                UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=folder.FullName,
                RedirectStandardOutput=true, RedirectStandardError=true, StandardOutputEncoding=Utf8, StandardErrorEncoding=Utf8
            };
            if (elevated)
            {
                start.Environment["PSModulePath"]=Path.Combine(psDirectory,"Modules");
                start.Environment["TEMP"]=start.Environment["TMP"]=folder.FullName;
                start.Environment["PATH"]=system+";"+Environment.GetFolderPath(Environment.SpecialFolder.Windows)+";"+psDirectory;
            }
            foreach (var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(folder.FullName,"Runner.ps1"),"-Mode",request.Mode,"-RequestPath",requestFile})
                start.ArgumentList.Add(arg);
            using var process=Process.Start(start) ?? throw new IOException("Could not start the disk backend.");
            var errors=process.StandardError.ReadToEndAsync();
            string line;
            while ((line=await process.StandardOutput.ReadLineAsync())!=null) report(line);
            await process.WaitForExitAsync();
            var error=await errors;
            if (!string.IsNullOrWhiteSpace(error)) report(error.Trim());
            return process.ExitCode;
        }
        finally { try { folder.Delete(true); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
    }
}
