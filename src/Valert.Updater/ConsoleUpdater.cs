using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using Valert.Core;

namespace Valert.Updater;

[SupportedOSPlatform("windows")]
public static class ConsoleUpdater
{
    public static async Task Run(string root)
    {
        // Installed metadata and the journal are administrator-owned. A web request supplies only a version.
        var install=Wire.Decode<ConsoleInstall>(File.ReadAllText(Path.Combine(root,"tools","console-install.json")));
        using var mutex=new Mutex(false,"Global\\vALERT.Console.Updater");if(!mutex.WaitOne(0))return;
        var host=new WindowsConsoleService(install);var engine=new ConsoleUpdateEngine(install,host);
        try
        {
            Directory.CreateDirectory(engine.Operations);await host.ProtectDirectory(engine.Operations);
            if(File.Exists(engine.JournalPath)){await engine.Recover();return;}
            var requestPath=Path.Combine(install.DataDirectory,"console-update-request.json");if(!File.Exists(requestPath))return;
            var request=Wire.Decode<ConsoleUpdateRequest>(File.ReadAllText(requestPath));
            File.Delete(requestPath); // A failed attempt requires an explicit administrator retry.
            if(!GitHubReleases.ValidVersion(request.Version))throw new InvalidDataException("Invalid requested version.");
            var command=host.Command;var executable=command.StartsWith('"')?command.Split('"')[1]:command;
            var installed=FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0]??"0.0.0";
            if(!Health.Newer(request.Version,installed)){engine.Status("Current","The console is already at this version or newer.",installed);return;}
            engine.Status("Downloading","Downloading and independently verifying the console release from GitHub.",request.Version);
            using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};http.DefaultRequestHeaders.UserAgent.ParseAdd("vALERT-ConsoleUpdater/"+Wire.Version);
            var github=new GitHubReleases(http);var release=await github.Version(request.Version,CancellationToken.None);
            var package=Path.Combine(engine.Operations,"console-package.zip");await github.Download(release.Assets["vALERT-Portal-win-x64.zip"],package,CancellationToken.None);
            await engine.Install(package,release.Version);File.Delete(package);
        }
        catch(Exception e){engine.Status("Failed",e.Message);Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
        finally{mutex.ReleaseMutex();}
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsConsoleService(ConsoleInstall install):IConsoleService
{
    public string Command => Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\vALERT.Portal","ImagePath",null)?.ToString()??throw new InvalidOperationException("Console service is not installed.");
    public async Task Stop()
    {
        // SERVICE_STOPPED can precede process exit. Wait for the original process so SQLite
        // cannot retain open handles while the snapshot or rollback is being prepared.
        var details=await CommandLine("sc.exe",["queryex","vALERT.Portal"],true);
        var pidMatch=System.Text.RegularExpressions.Regex.Match(details,@"PID\s*:\s*(\d+)");
        Process? tracked=null;
        if(pidMatch.Success&&int.TryParse(pidMatch.Groups[1].Value,out var pid)&&pid>0)
            try{tracked=Process.GetProcessById(pid);}catch(ArgumentException){}
        using var process=tracked;
        await CommandLine("sc.exe",["stop","vALERT.Portal"],true);
        for(var i=0;i<60;i++){
            var state=await CommandLine("sc.exe",["query","vALERT.Portal"]);
            if(state.Contains("STOPPED",StringComparison.Ordinal)){
                if(process is not null){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));await process.WaitForExitAsync(timeout.Token);}
                return;
            }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("Console did not stop. Data was not replaced.");
    }
    public async Task SetCommand(string command)=>await CommandLine("sc.exe",["config","vALERT.Portal","binPath=",command]);
    public async Task Start()=>await CommandLine("sc.exe",["start","vALERT.Portal"],true);
    public async Task<bool> Healthy(string version)
    {
        var port=5080;var settings=Path.Combine(install.DataDirectory,"portal-settings.json");
        if(File.Exists(settings)){using var json=JsonDocument.Parse(File.ReadAllText(settings));if(json.RootElement.TryGetProperty("localPort",out var p))port=p.GetInt32();}
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
        for(var i=0;i<45;i++)
        {
            try{using var json=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health"));if(json.RootElement.GetProperty("version").GetString()==version)return true;}catch(HttpRequestException){}catch(TaskCanceledException){}catch(JsonException){}
            await Task.Delay(1000);
        }
        return false;
    }
    public async Task ProtectDirectory(string directory)=>await CommandLine("icacls.exe",[directory,"/inheritance:r","/grant:r","*S-1-5-18:(OI)(CI)F","*S-1-5-32-544:(OI)(CI)F"]);
    public async Task RestoreDataAccess(string directory)=>await CommandLine("icacls.exe",[directory,"/grant:r","*S-1-5-19:(OI)(CI)M"]);
    static async Task<string> CommandLine(string executable,string[] args,bool allowFailure=false)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("Cannot start Windows service utility.");var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));await process.WaitForExitAsync(timeout.Token);var output=await stdout;await stderr;
        if(process.ExitCode!=0&&!allowFailure)throw new InvalidOperationException("Windows service utility failed: "+executable+" ("+process.ExitCode+")");return output;
    }
}
