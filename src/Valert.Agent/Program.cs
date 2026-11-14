using System.Net.Http.Headers;
using System.Net.Http.Json;
using Valert.Agent;
using Valert.Core;

var data = Environment.GetEnvironmentVariable("VALERT_AGENT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"vALERT","Agent");
Directory.CreateDirectory(data);
var settingsPath=Path.Combine(data,"agent-settings.json");
if(args.Contains("--configure"))
{
    if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Configure the agent on the Windows Veeam server.");
    var index=Array.IndexOf(args,"--configure"); var enrollmentPath=index+1<args.Length?args[index+1]:"";
    var existing=File.Exists(settingsPath)?Wire.Decode<AgentSettings>(File.ReadAllText(settingsPath)):new AgentSettings();
    string Prompt(string text,string fallback="") { Console.Write(text+(fallback.Length>0?$" [{fallback}]":"")+": "); var value=Console.ReadLine(); return string.IsNullOrWhiteSpace(value)?fallback:value.Trim(); }
    string Password(string text) { Console.Write(text+": "); var password=""; ConsoleKeyInfo key; while((key=Console.ReadKey(true)).Key!=ConsoleKey.Enter) { if(key.Key==ConsoleKey.Backspace) { if(password.Length>0)password=password[..^1]; } else if(!char.IsControl(key.KeyChar))password+=key.KeyChar; } Console.WriteLine(); return password; }
    Console.WriteLine("vALERT agent setup — credentials stay on this Windows server.");
    existing.VeeamUrl=Prompt("Veeam REST HTTPS address",existing.VeeamUrl);
    existing.VeeamUser=Prompt("Dedicated Veeam service-account username",existing.VeeamUser);
    var password=Password("Veeam password (blank preserves existing credential)"); if(password.Length>0)existing.VeeamPasswordProtected=AgentSettings.Protect(password);
    existing.VeeamCertificateSha256=Prompt("Optional Veeam certificate SHA-256 fingerprint (for a verified self-signed certificate)",existing.VeeamCertificateSha256);
    existing.OneUrl=Prompt("Optional Veeam ONE HTTPS address (blank skips)",existing.OneUrl);
    if(existing.OneUrl.Length>0)
    {
        Console.WriteLine("Use a Veeam ONE endpoint dedicated to this customer. Its alarms and inventory will be visible to that customer's viewers.");
        existing.OneUser=Prompt("Veeam ONE service-account username",existing.OneUser); var onePassword=Password("Veeam ONE password (blank preserves existing)"); if(onePassword.Length>0)existing.OnePasswordProtected=AgentSettings.Protect(onePassword);
        existing.OneCertificateSha256=Prompt("Optional ONE certificate SHA-256 fingerprint",existing.OneCertificateSha256); existing.OneApiVersion=Prompt("ONE REST API version",existing.OneApiVersion);
    }
    existing.UsePowerShell=Prompt("Use optional PowerShell compatibility collection? Requires a Veeam Backup Administrator service identity (yes/no)",existing.UsePowerShell?"yes":"no").Equals("yes",StringComparison.OrdinalIgnoreCase);
    if(existing.UsePowerShell)existing.PowerShellPath=Prompt("Full path to the PowerShell executable required by your Veeam version",existing.PowerShellPath);
    if(!string.IsNullOrWhiteSpace(enrollmentPath)&&File.Exists(enrollmentPath))
    {
        var enrollment=Wire.Decode<Enrollment>(File.ReadAllText(enrollmentPath)); if(enrollment.ExpiresAt<DateTimeOffset.UtcNow)throw new InvalidOperationException("Enrollment expired. Generate another package in the portal.");
        using var portal=VeeamCollector.Client(enrollment.PortalUrl); using var response=await portal.PostAsJsonAsync("api/agents/enroll",new EnrollmentRequest(enrollment.Token,Environment.MachineName,Wire.Version),Wire.Json); response.EnsureSuccessStatusCode();
        var registered=await response.Content.ReadFromJsonAsync<EnrollmentResponse>(Wire.Json)??throw new InvalidDataException("No enrollment response.");
        existing.PortalUrl=enrollment.PortalUrl;existing.AgentId=registered.AgentId;existing.CredentialProtected=AgentSettings.Protect(registered.Credential);existing.UpdatePublicKey=enrollment.UpdatePublicKey;
        var rootIndex=Array.IndexOf(args,"--install-root"); var installRoot=rootIndex>=0?args[rootIndex+1]:Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..",".."));
        Security.AtomicWrite(Path.Combine(installRoot,"trust.json"),Wire.Encode(new { portalUrl=existing.PortalUrl,updatePublicKey=existing.UpdatePublicKey,dataDirectory=data }));
    }
    if(existing.AgentId.Length==0)throw new InvalidOperationException("A customer enrollment package is required for first installation.");
    Security.AtomicWrite(settingsPath,Wire.Encode(existing));Console.WriteLine("Configuration saved. The service will report automatically. If using PowerShell, configure the service to run as the dedicated Veeam-authorized account.");return;
}
if(args.Contains("--self-test")) { Console.WriteLine("vALERT.Agent "+Wire.Version+" ready");return; }
var builder=Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o=>o.ServiceName="vALERT.Agent");
builder.Services.AddSingleton(new AgentPaths(data,settingsPath));builder.Services.AddHostedService<CollectorWorker>();
await builder.Build().RunAsync();

namespace Valert.Agent
{
    public sealed record AgentPaths(string Data,string SettingsPath);
    public sealed class CollectorWorker(AgentPaths paths,ILogger<CollectorWorker> logger):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Security.AtomicWrite(Path.Combine(paths.Data,"ready.json"),Wire.Encode(new { version=Wire.Version,startedAt=DateTimeOffset.UtcNow,pid=Environment.ProcessId }));
            var queue=new DurableQueue(paths.Data);
            while(!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if(!File.Exists(paths.SettingsPath))throw new InvalidOperationException("Agent is awaiting configuration. Run the installer with a customer enrollment package.");
                    var settings=Wire.Decode<AgentSettings>(File.ReadAllText(paths.SettingsPath));
                    AgentReport report;
                    try
                    {
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);timeout.CancelAfter(TimeSpan.FromMinutes(45));
                        report=await new VeeamCollector(settings).Collect(timeout.Token);
                    }
                    catch(Exception e) when(!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning("Collection failed: {Reason}",e.Message);report=new AgentReport { CollectionSucceeded=false,InventoryComplete=false,Issues=[e.Message] }; }
                    var updateFile=Path.Combine(paths.Data,"update-status.txt");
                    report=report with { PendingReports=queue.Count,UpdateStatus=File.Exists(updateFile)?File.ReadAllText(updateFile):"Current" };
                    var batches=report.Runs.Chunk(200).ToList();
                    if(batches.Count==0)queue.Add(report);
                    else for(var i=0;i<batches.Count;i++)queue.Add(report with { ReportId=report.ReportId+"-"+i,Runs=batches[i].ToList() });
                    // Advance the collection cursor only after the full report is durably queued.
                    if(report.CollectionSucceeded) { settings.LastHistoryScan=report.CollectedAt;settings.OpenSessionIds=report.Runs.Where(r=>r.EndedAt is null).Select(r=>r.Id).ToList();Security.AtomicWrite(paths.SettingsPath,Wire.Encode(settings)); }
                    using var portal=VeeamCollector.Client(settings.PortalUrl);portal.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",AgentSettings.Unprotect(settings.CredentialProtected));
                    foreach(var file in queue.Files())
                    {
                        var queued=Wire.Decode<AgentReport>(await File.ReadAllTextAsync(file,stoppingToken));
                        using var response=await portal.PostAsJsonAsync("api/agents/reports",queued,Wire.Json,stoppingToken);
                        if(!response.IsSuccessStatusCode)throw new HttpRequestException("Portal returned HTTP "+(int)response.StatusCode+"; queued reports retained.");
                        var acknowledgement=await response.Content.ReadFromJsonAsync<AgentResponse>(Wire.Json,stoppingToken)??throw new InvalidDataException("No report acknowledgement.");
                        queue.Acknowledge(file,queued.ReportId,acknowledgement);
                        if(acknowledgement.Update is { } update&&(settings.AutomaticUpdates||acknowledgement.UpdateRequested))
                        {
                            if(!Security.VerifyManifest(update,settings.UpdatePublicKey))throw new InvalidDataException("Update signature did not match the enrolled portal key.");
                            var updateUri=new Uri(update.Url);var portalUri=new Uri(settings.PortalUrl);
                            if(updateUri.Scheme!="https"||updateUri.Authority!=portalUri.Authority)throw new InvalidDataException("Update URL does not match enrolled portal.");
                            // A separately installed SYSTEM scheduled task validates the immutable trust file and applies this request.
                            var pendingPath=Path.Combine(paths.Data,"pending-update.json");
                            // Later backlog acknowledgements must not erase an administrator's retry request.
                            if(acknowledgement.UpdateRequested || !File.Exists(pendingPath))
                                Security.AtomicWrite(pendingPath,Wire.Encode(new { manifest=update,force=acknowledgement.UpdateRequested }));
                            Security.AtomicWrite(updateFile,"Update queued: "+update.Version);
                        }
                    }
                    logger.LogInformation("Collection completed; {Count} reports remain queued.",queue.Count);
                }
                catch(Exception e) when(!stoppingToken.IsCancellationRequested) { logger.LogWarning("Agent cycle failed; data retained: {Reason}",e.Message); }
                try{await Task.Delay(TimeSpan.FromMinutes(15),stoppingToken);}catch(OperationCanceledException){break;}
            }
        }
    }
}
