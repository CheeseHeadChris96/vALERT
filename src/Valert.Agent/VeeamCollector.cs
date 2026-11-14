using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valert.Core;

namespace Valert.Agent;

public sealed class VeeamCollector(AgentSettings settings)
{
    public static readonly string[] Revisions = ["1.3-rev2","1.3-rev1","1.3-rev0","1.2-rev1","1.2-rev0","1.1-rev2","1.1-rev1","1.1-rev0"];
    TimeZoneInfo zone = TimeZoneInfo.Local;
    readonly List<string> issues = [];
    public static HttpClient Client(string url,string pinnedSha256 = "")
    {
        if (!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Collector endpoints must use HTTPS.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrWhiteSpace(pinnedSha256))
        {
            var pin = pinnedSha256.Replace(":", "").Replace(" ", "").ToUpperInvariant();
            if (pin.Length != 64 || pin.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Use the SHA-256 certificate fingerprint (64 hexadecimal characters).");
            handler.ServerCertificateCustomValidationCallback = (_,cert,_,errors) => cert is not null && cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() >= DateTime.UtcNow && Convert.ToHexString(SHA256.HashData(cert.RawData)) == pin;
        }
        return new HttpClient(handler) { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
    }
    public async Task<AgentReport> Collect(CancellationToken token)
    {
        issues.Clear();
        if (settings.UsePowerShell) return await PowerShellCollect(token);
        using var client = Client(settings.VeeamUrl,settings.VeeamCertificateSha256);
        string? revision = null;
        foreach (var candidate in Revisions)
        {
            client.DefaultRequestHeaders.Remove("x-api-version"); client.DefaultRequestHeaders.Add("x-api-version",candidate);
            using var response = await client.GetAsync("api/v1/serverTime",token);
            if (response.IsSuccessStatusCode) { revision = candidate; var clock = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement; var timezone = Text(clock,"ianaTimeZoneId") ?? Text(clock,"timeZone"); try { if (timezone is not null) zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); } catch (TimeZoneNotFoundException) { issues.Add("Veeam timezone was not recognized; using this Windows server's timezone."); } break; }
            var body = await response.Content.ReadAsStringAsync(token);
            if ((response.StatusCode == HttpStatusCode.BadRequest || (int)response.StatusCode == 501) && body.Contains("version",StringComparison.OrdinalIgnoreCase)) continue;
            if (response.StatusCode == HttpStatusCode.Unauthorized) { revision = candidate; break; }
            throw new InvalidOperationException("Veeam API negotiation failed: HTTP " + (int)response.StatusCode + ". Check the configured REST endpoint.");
        }
        if (revision is null) throw new InvalidOperationException("No compatible Veeam REST API revision was found.");
        using var login = await client.PostAsync("api/oauth2/token",new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"]="password",["username"]=settings.VeeamUser,["password"]=AgentSettings.Unprotect(settings.VeeamPasswordProtected) }),token);
        if (!login.IsSuccessStatusCode) throw new InvalidOperationException("Veeam authentication failed (HTTP " + (int)login.StatusCode + "). Check the dedicated service account and its role.");
        var authentication = JsonDocument.Parse(await login.Content.ReadAsStringAsync(token)).RootElement; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",Text(authentication,"access_token"));
        var jobsRaw = await List(client,"api/v1/jobs",token); var states = (await List(client,"api/v1/jobs/states",token)).GroupBy(j => Text(j,"id") ?? "").ToDictionary(g => g.Key,g => g.First());
        var jobs = jobsRaw.Select(j => MapJob(j,states.GetValueOrDefault(Text(j,"id") ?? ""))).ToList();
        if (revision != "1.3-rev2") issues.Add("REST API " + revision + " exposes a limited set of job types. Configure the optional PowerShell compatibility collector if this server has job types not represented here.");
        string? version = null; string? serverId = null;
        try { var server = await Get(client,"api/v1/serverInfo",token); version = Text(server,"buildVersion"); serverId = Text(server,"vbrId"); }
        catch (HttpRequestException) { version = LocalVersion(); if (version is null) issues.Add("Veeam build information is unavailable to this role; installed file version was not found."); }
        var since = settings.LastHistoryScan?.AddDays(-2) ?? DateTimeOffset.UtcNow.AddDays(-90);
        var sessions = await List(client,"api/v1/sessions?createdAfterFilter=" + Uri.EscapeDataString(since.ToString("O")),token);
        foreach (var id in settings.OpenSessionIds.Where(id => sessions.All(s => Text(s,"id") != id)))
        { try { sessions.Add(await Get(client,"api/v1/sessions/" + Uri.EscapeDataString(id),token)); } catch (HttpRequestException) { issues.Add("A previously running session could not be refreshed: " + id); } }
        var runs = new List<JobRun>(); var taskSupport = !revision.StartsWith("1.1",StringComparison.Ordinal); bool taskFailure = false;
        foreach (var session in sessions)
        {
            var id = Text(session,"id"); var jobId = Text(session,"jobId"); var start = Date(session,"creationTime"); if (id is null || jobId is null || start is null) continue;
            var end = Date(session,"endTime"); var resultElement = Prop(session,"result"); var result = Text(resultElement,"result") ?? Text(session,"result");
            var vms = new List<VmResult>();
            if (taskSupport && !taskFailure)
            {
                try { foreach (var task in await List(client,"api/v1/sessions/" + Uri.EscapeDataString(id) + "/taskSessions",token)) vms.Add(new(Text(task,"name") ?? "Unknown",Health.Result(Text(Prop(task,"result"),"result") ?? Text(task,"result")),Date(task,"creationTime"),Date(task,"endTime"),Text(Prop(task,"result"),"message"))); }
                catch (HttpRequestException e) { taskFailure = true; issues.Add("Per-VM task details are unavailable: " + e.Message); }
            }
            runs.Add(new JobRun { Id=id,JobId=jobId,StartedAt=start.Value,EndedAt=end,Result=Bool(resultElement,"isCanceled") ? "Failed" : Health.Result(result),Message=Text(resultElement,"message"),Vms=vms });
        }
        if (!taskSupport) issues.Add("This Veeam version does not expose per-VM task details through REST. Use the optional PowerShell collector for those details.");
        var repositories = new List<RepositoryInfo>();
        try
        {
            foreach (var r in await List(client,"api/v1/backupInfrastructure/repositories/states",token))
            {
                var unusable = Prop(r,"isOnline").ValueKind == JsonValueKind.False || Bool(r,"isOutOfDate");
                repositories.Add(new(Text(r,"id") ?? Text(r,"repositoryId") ?? "unknown",Text(r,"name") ?? "Repository",Text(r,"type") ?? "Repository",unusable ? null : Number(r,"capacityGB") * 1024 * 1024 * 1024,unusable ? null : Number(r,"freeGB") * 1024 * 1024 * 1024));
            }
        }
        catch (HttpRequestException e) { issues.Add("Repository capacity unavailable: " + e.Message); }
        var report = new AgentReport { ServerName=Environment.MachineName,VeeamVersion=version,VeeamServerId=serverId,InventoryComplete=true,CollectionSucceeded=true,Jobs=jobs,Runs=runs,Repositories=repositories,Issues=issues };
        return await AddOne(report,token);
    }
    async Task<AgentReport> AddOne(AgentReport report,CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.OneUrl)) return report;
        try
        {
            using var one = Client(settings.OneUrl,settings.OneCertificateSha256);
            using var login = await one.PostAsync("api/token",new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"]="password",["username"]=settings.OneUser,["password"]=AgentSettings.Unprotect(settings.OnePasswordProtected) }),token);
            login.EnsureSuccessStatusCode(); var auth = JsonDocument.Parse(await login.Content.ReadAsStringAsync(token)).RootElement;
            one.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",Text(auth,"access_token"));
            var root = "api/v" + settings.OneApiVersion + "/";
            var alarms = await List(one,root + "alarms/triggeredAlarms",token,true);
            var metrics = new Dictionary<string,List<JsonElement>>();
            foreach (var path in new[] { "vbr/repositories", "vbr/scaleoutRepositories", "vbr/backupServers", "vbr/backupProxies" })
            { try { metrics[path] = await List(one,root + path,token,true); } catch (HttpRequestException) { issues.Add("Veeam ONE information unavailable: " + path); } }
            return report with { OneConnected=true,OneAlarms=alarms.Select(a => new OneAlarm(Text(a,"id") ?? Text(a,"alarmId") ?? "",Text(a,"name") ?? Text(a,"alarmName") ?? "Veeam ONE alarm",Text(a,"severity") ?? Text(a,"status") ?? "Unknown",Text(a,"objectName"),Date(a,"time") ?? Date(a,"triggeredTime"),Text(a,"description") ?? Text(a,"message"))).ToList(),OneMetrics=JsonSerializer.SerializeToElement(metrics,Wire.Json) };
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException) { issues.Add("Veeam ONE collection failed: " + e.Message); return report; }
    }
    JobInfo MapJob(JsonElement job,JsonElement state)
    {
        var schedule = Prop(job,"schedule"); var scheduled = Bool(schedule,"runAutomatically"); var next = Date(state,"nextRun"); var previous = PreviousDue(schedule,zone,DateTimeOffset.UtcNow);
        var kind = !scheduled ? "Manual" : Bool(Prop(schedule,"daily"),"isEnabled") ? "Daily" : Bool(Prop(schedule,"monthly"),"isEnabled") ? "Monthly" : Bool(Prop(schedule,"periodically"),"isEnabled") ? "Periodic" : Bool(Prop(schedule,"afterThisJob"),"isEnabled") ? "Chained" : "Scheduled";
        return new JobInfo { Id=Text(job,"id") ?? throw new InvalidDataException("Veeam job has no identifier."),Name=Text(job,"name") ?? "Unnamed job",Type=Text(job,"type") ?? "Unknown",Enabled=!Bool(job,"isDisabled"),Scheduled=scheduled,ScheduleFingerprint=Security.Hash(schedule.ValueKind==JsonValueKind.Undefined?"":schedule.GetRawText()),ScheduleDescription=kind,ScheduleIssue=scheduled&&next is null&&previous is null?"Expected run is not available; a schedule baseline or manual investigation is needed.":null,NextRun=next,PreviousDue=previous,LastStart=Date(state,"lastRun"),Running=new[]{"Running","Starting","Working","Stopping","Pausing","Resuming"}.Contains(Text(state,"status")),ObjectCount=(int)(Number(state,"objectsCount") ?? 0),RepositoryName=Text(state,"repositoryName") };
    }
    public static DateTimeOffset? PreviousDue(JsonElement schedule,TimeZoneInfo zone,DateTimeOffset now)
    {
        if (!Bool(schedule,"runAutomatically")) return null;
        var daily=Prop(schedule,"daily"); var monthly=Prop(schedule,"monthly"); var local=TimeZoneInfo.ConvertTime(now,zone).DateTime;
        var recurrence=Bool(daily,"isEnabled")?daily:Bool(monthly,"isEnabled")?monthly:default;
        if (recurrence.ValueKind==JsonValueKind.Undefined || !TimeOnly.TryParse(Text(recurrence,"localTime"),CultureInfo.InvariantCulture,out var time)) return null;
        for(var offset=0;offset<370;offset++)
        {
            var date=DateOnly.FromDateTime(local).AddDays(-offset); var at=date.ToDateTime(time,DateTimeKind.Unspecified); if(at>local) continue;
            if(Bool(daily,"isEnabled"))
            {
                var kind=Text(daily,"dailyKind"); var days=Prop(daily,"days");
                if(kind=="WeekDays" && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if(days.ValueKind==JsonValueKind.Array && days.GetArrayLength()>0 && !days.EnumerateArray().Any(d=>string.Equals(d.GetString(),date.DayOfWeek.ToString(),StringComparison.OrdinalIgnoreCase))) continue;
            }
            else
            {
                var months=Prop(monthly,"months"); if(months.ValueKind==JsonValueKind.Array && months.GetArrayLength()>0 && !months.EnumerateArray().Any(m=>string.Equals(m.ToString(),CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month),StringComparison.OrdinalIgnoreCase)||m.ToString()==date.Month.ToString())) continue;
                var ordinal=Text(monthly,"dayNumberInMonth");
                if(ordinal=="OnDay" || (Number(monthly,"dayOfMonth") is not null && string.IsNullOrEmpty(ordinal))) { if(date.Day!=(int)(Number(monthly,"dayOfMonth")??1)) continue; }
                else
                {
                    if(!string.Equals(Text(monthly,"dayOfWeek"),date.DayOfWeek.ToString(),StringComparison.OrdinalIgnoreCase)) continue;
                    var n=(date.Day-1)/7+1; if(ordinal=="Last" ? date.AddDays(7).Month==date.Month : n!=(ordinal switch {"First"=>1,"Second"=>2,"Third"=>3,"Fourth"=>4,_=>-1})) continue;
                }
            }
            if(zone.IsInvalidTime(at)) continue;
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(at,zone),TimeSpan.Zero);
        }
        return null;
    }
    async Task<AgentReport> PowerShellCollect(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.PowerShellPath) || !File.Exists(settings.PowerShellPath)) throw new ArgumentException("Set the full path to the PowerShell version required by this Veeam installation.");
        var start=new ProcessStartInfo(settings.PowerShellPath){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var arg in new[]{"-NoLogo","-NonInteractive","-NoProfile","-File",Path.Combine(AppContext.BaseDirectory,"Collect-Veeam.ps1"),"-Since",(settings.LastHistoryScan?.AddDays(-2)??DateTimeOffset.UtcNow.AddDays(-90)).ToString("O")}) start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("Could not start PowerShell."); var output=process.StandardOutput.ReadToEndAsync(token); var error=process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); } catch { process.Kill(true); throw; }
        if(process.ExitCode!=0) throw new InvalidOperationException("PowerShell collection failed. Check the service identity's Veeam role and installed PowerShell version. " + (await error)[..Math.Min((await error).Length,500)]);
        var report=Wire.Decode<AgentReport>(await output); issues.AddRange(report.Issues); return await AddOne(report with { Issues=issues },token);
    }
    public static async Task<JsonElement> Get(HttpClient client,string path,CancellationToken token)
    {
        using var response=await client.GetAsync(path,token); if(!response.IsSuccessStatusCode) throw new HttpRequestException($"{path.Split('?')[0]} returned HTTP {(int)response.StatusCode}.",null,response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement.Clone();
    }
    public static async Task<List<JsonElement>> List(HttpClient client,string path,CancellationToken token,bool one=false)
    {
        var list=new List<JsonElement>(); const int limit=500;
        for(var skip=0;skip<100000;skip+=limit)
        {
            var result=await Get(client,path+(path.Contains('?')?"&":"?")+(one?"Offset":"skip")+"="+skip+"&"+(one?"Limit":"limit")+"="+limit,token);
            var data=result.ValueKind==JsonValueKind.Array?result:Prop(result,one?"items":"data");
            if(data.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("Unexpected list response from "+path.Split('?')[0]);
            list.AddRange(data.EnumerateArray().Select(x=>x.Clone())); if(data.GetArrayLength()<limit) return list;
        }
        throw new InvalidDataException("Collection exceeded 100,000 records; narrow source history before retrying.");
    }
    public static JsonElement Prop(JsonElement e,string name) => e.ValueKind==JsonValueKind.Object && e.TryGetProperty(name,out var value)?value:default;
    public static string? Text(JsonElement e,string name) { var p=Prop(e,name); return p.ValueKind==JsonValueKind.String?p.GetString():p.ValueKind==JsonValueKind.Number?p.ToString():null; }
    public static bool Bool(JsonElement e,string name)=>Prop(e,name).ValueKind==JsonValueKind.True;
    public static double? Number(JsonElement e,string name)=>Prop(e,name).ValueKind==JsonValueKind.Number?Prop(e,name).GetDouble():null;
    DateTimeOffset? Date(JsonElement e,string name)
    {
        var text=Text(e,name); if(string.IsNullOrWhiteSpace(text)||text.StartsWith("0001",StringComparison.Ordinal))return null;
        if(DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var parsed))
        {
            if(text.EndsWith('Z')||text.LastIndexOf('+')>9||text.LastIndexOf('-')>9) return parsed.ToUniversalTime();
            if(DateTime.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var local)) return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local,DateTimeKind.Unspecified),zone),TimeSpan.Zero);
        }
        return null;
    }
    static string? LocalVersion()
    {
        var root=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach(var file in new[]{Path.Combine(root,"Veeam","Backup and Replication","Backup","Veeam.Backup.Service.exe"),Path.Combine(root,"Veeam","Backup and Replication","Console","Veeam.Backup.Shell.exe")}) if(File.Exists(file)) return FileVersionInfo.GetVersionInfo(file).FileVersion;
        return null;
    }
}
