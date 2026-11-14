using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Valert.Core;

if(args.Contains("--self-test")){Console.WriteLine("vALERT.Updater "+Wire.Version+" ready");return;}
if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("The updater runs on Windows.");
var rootIndex=Array.IndexOf(args,"--root");var root=rootIndex>=0?Path.GetFullPath(args[rootIndex+1]):Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
if(args.Contains("--console")){await Valert.Updater.ConsoleUpdater.Run(root);return;}
var trust=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"trust.json"))).RootElement;
var data=trust.GetProperty("dataDirectory").GetString()!;var publicKey=trust.GetProperty("updatePublicKey").GetString()!;var portalUrl=ConsoleTransport.Origin(trust.GetProperty("portalUrl").GetString()!);
Directory.CreateDirectory(data);
using var mutex=new Mutex(false,"Global\\vALERT.Agent.Updater");if(!mutex.WaitOne(0))return;
var journalPath=Path.Combine(root,"update-journal.json");
if(File.Exists(journalPath))
{
    var journal=JsonDocument.Parse(File.ReadAllText(journalPath)).RootElement;
    await Run("sc.exe",["stop","vALERT.Agent"],TimeSpan.FromSeconds(30),true);await WaitStopped();
    await Run("sc.exe",["config","vALERT.Agent","binPath=",journal.GetProperty("oldCommand").GetString()!],TimeSpan.FromSeconds(30));
    await Run("sc.exe",["start","vALERT.Agent"],TimeSpan.FromSeconds(30));
    Security.AtomicWrite(Path.Combine(root,"failed-update.txt"),journal.GetProperty("version").GetString()!);
    Security.AtomicWrite(Path.Combine(data,"update-status.txt"),"Interrupted update recovered; previous agent restored. Use Update / retry in the portal.");
    File.Delete(journalPath);
}
var requestPath=Path.Combine(data,"pending-update.json");if(!File.Exists(requestPath))return;
var status=Path.Combine(data,"update-status.txt");
try
{
    var request=JsonDocument.Parse(File.ReadAllText(requestPath)).RootElement;
    var manifest=Wire.Decode<UpdateManifest>(request.GetProperty("manifest").GetRawText());
    var force=request.TryGetProperty("force",out var requested)&&requested.GetBoolean();
    if(!Security.VerifyManifest(manifest,publicKey))throw new InvalidDataException("Invalid update signature.");
    var uri=new Uri(manifest.Url);if(uri.Scheme!="https"||uri.Authority!=portalUrl.Authority||manifest.Size>500_000_000)throw new InvalidDataException("Invalid update origin or package size.");
    if(manifest.Version.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='.'&&c!='-')||manifest.Version.Contains(".."))throw new InvalidDataException("Invalid update version.");
    var failedPath=Path.Combine(root,"failed-update.txt");
    if(!force&&File.Exists(failedPath)&&File.ReadAllText(failedPath)==manifest.Version){File.Delete(requestPath);return;}
    var oldCommand=Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\vALERT.Agent","ImagePath",null)?.ToString()??throw new InvalidOperationException("Agent service is not installed.");
    var oldExe=oldCommand.StartsWith('"')?oldCommand.Split('"')[1]:oldCommand;
    var installedVersion=FileVersionInfo.GetVersionInfo(oldExe).ProductVersion?.Split('+')[0]??"0.0.0";
    if(!Health.Newer(manifest.Version,installedVersion)){File.Delete(requestPath);return;}
    var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(data,"agent-settings.json"))).RootElement;
    var encrypted=Convert.FromBase64String(settings.GetProperty("credentialProtected").GetString()!);
    var credential=System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(encrypted,null,DataProtectionScope.LocalMachine));
    var staging=Path.Combine(root,"staging");Directory.CreateDirectory(staging);var package=Path.Combine(staging,"agent.zip");
    using(var client=ConsoleTransport.CreateClient(portalUrl.AbsoluteUri,TimeSpan.FromMinutes(10)))
    {
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",credential);
        using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead);response.EnsureSuccessStatusCode();
        await using var output=File.Create(package);await response.Content.CopyToAsync(output);
    }
    if(new FileInfo(package).Length!=manifest.Size||Security.HashFile(package)!=manifest.Sha256.ToLowerInvariant())throw new InvalidDataException("Update package checksum mismatch.");
    var target=Path.Combine(root,"versions",manifest.Version);if(Directory.Exists(target))Directory.Delete(target,true);Directory.CreateDirectory(target);
    using(var zip=ZipFile.OpenRead(package))
    {
        foreach(var entry in zip.Entries)
        {
            var destination=Path.GetFullPath(Path.Combine(target,entry.FullName));
            if(!destination.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsafe update archive path.");
            if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(destination);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);entry.ExtractToFile(destination,false);
        }
    }
    var newExe=Path.Combine(target,"Valert.Agent.exe");if(!File.Exists(newExe))throw new InvalidDataException("Update has no agent executable.");
    await Run(newExe,["--self-test"],TimeSpan.FromSeconds(30));
    // Keep the service command and update request recovery journal outside the mutable agent data directory.
    Security.AtomicWrite(Path.Combine(root,"update-journal.json"),Wire.Encode(new { oldCommand,newCommand="\""+newExe+"\"",version=manifest.Version }));
    Security.AtomicWrite(status,"Installing "+manifest.Version);
    await Run("sc.exe",["stop","vALERT.Agent"],TimeSpan.FromSeconds(30),allowFailure:true);await WaitStopped();
    await Run("sc.exe",["config","vALERT.Agent","binPath=","\""+newExe+"\""],TimeSpan.FromSeconds(30));
    var readyPath=Path.Combine(data,"ready.json");if(File.Exists(readyPath))File.Delete(readyPath);
    try
    {
        await Run("sc.exe",["start","vALERT.Agent"],TimeSpan.FromSeconds(30));
        var ready=false;
        for(var i=0;i<30;i++){await Task.Delay(2000);if(File.Exists(readyPath)){var health=JsonDocument.Parse(File.ReadAllText(readyPath)).RootElement;if(health.GetProperty("version").GetString()==manifest.Version){ready=true;break;}}}
        if(!ready)throw new InvalidOperationException("Updated agent did not become ready.");
        Security.AtomicWrite(status,"Current: "+manifest.Version);File.Delete(requestPath);File.Delete(Path.Combine(root,"update-journal.json"));File.Delete(package);
    }
    catch
    {
        await Run("sc.exe",["stop","vALERT.Agent"],TimeSpan.FromSeconds(30),true);await WaitStopped();
        await Run("sc.exe",["config","vALERT.Agent","binPath=",oldCommand],TimeSpan.FromSeconds(30));await Run("sc.exe",["start","vALERT.Agent"],TimeSpan.FromSeconds(30));
        if(File.Exists(journalPath))File.Delete(journalPath);
        Security.AtomicWrite(failedPath,manifest.Version);throw new InvalidOperationException("Update failed startup validation; previous agent restored. Configuration and queue were preserved.");
    }
}
catch(Exception e){Security.AtomicWrite(status,"Update failed: "+e.Message);Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
finally{mutex.ReleaseMutex();}

static async Task Run(string executable,string[] arguments,TimeSpan timeout,bool allowFailure=false)
{
    var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true};foreach(var a in arguments)start.ArgumentList.Add(a);
    using var process=Process.Start(start)??throw new InvalidOperationException("Cannot start "+executable);var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    using var cancel=new CancellationTokenSource(timeout);await process.WaitForExitAsync(cancel.Token);await stdout;await stderr;if(process.ExitCode!=0&&!allowFailure)throw new InvalidOperationException(executable+" failed with exit code "+process.ExitCode);
}
static async Task WaitStopped()
{
    for(var i=0;i<30;i++){await Task.Delay(1000);var start=new ProcessStartInfo("sc.exe"){UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};start.ArgumentList.Add("query");start.ArgumentList.Add("vALERT.Agent");using var process=Process.Start(start)!;var output=await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();if(output.Contains("STOPPED",StringComparison.Ordinal))return;}
    throw new InvalidOperationException("Agent did not stop. Update aborted without replacing data.");
}
