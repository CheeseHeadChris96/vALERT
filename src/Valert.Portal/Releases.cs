using System.Security.Cryptography;
using Valert.Core;

namespace Valert.Portal;

public sealed class Releases(Store store, Platform platform, ILogger<Releases> logger):BackgroundService
{
    readonly HttpClient http=CreateClient();
    readonly SemaphoreSlim gate=new(1,1);
    static HttpClient CreateClient(){var client=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};client.DefaultRequestHeaders.UserAgent.ParseAdd("vALERT/"+Wire.Version);return client;}
    public UpdateManifest? Current { get; private set; }
    public PublishedRelease? Published { get; private set; }
    public string? PackagePath { get; private set; }
    public string? InstallerPath { get; private set; }
    public string? InstallerHash { get; private set; }
    public string? ConsoleInstallerPath { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public string Status { get; private set; }="Waiting for release check";
    public object ConsoleState(ConsoleUpdates updates)=>new {
        installed=Wire.Version,available=Published?.Version,updateAvailable=Published is not null&&Health.Newer(Published.Version,Wire.Version),
        lastChecked=LastChecked,nextCheck=platform.Settings.AutomaticUpdateChecks?LastChecked?.AddHours(1):null,
        automaticChecks=platform.Settings.AutomaticUpdateChecks,downloadReady=ConsoleInstallerPath is not null&&File.Exists(ConsoleInstallerPath),
        releaseUrl=Published?.PageUrl,publishedAt=Published?.PublishedAt,canInstall=updates.Available,operation=updates.Status()
    };
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            if(!ConsoleUpdates.Held&&!platform.Settings.MigrationPaused)
            {
                if(platform.Settings.AutomaticUpdateChecks&&(LastChecked is null||DateTimeOffset.UtcNow-LastChecked>=TimeSpan.FromHours(1)))
                    try{await Refresh(token);}catch(Exception e)when(!token.IsCancellationRequested){logger.LogWarning("Release check failed: {Reason}",e.Message);}
                store.Retain(DateTimeOffset.UtcNow);
            }
            try{await Task.Delay(TimeSpan.FromMinutes(1),token);}catch(OperationCanceledException){break;}
        }
    }
    public async Task Refresh(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            Status="Checking GitHub releases";
            var github=new GitHubReleases(http);var release=await github.Latest(platform.Settings.IncludePreviewUpdates,token);
            if(release is null){Published=null;Current=null;ConsoleInstallerPath=null;Status="No published release matches your update settings";return;}
            if(Published?.Version!=release.Version)ConsoleInstallerPath=null;
            Published=release;var cache=Path.Combine(store.DataDirectory,"release-cache",release.Version);Directory.CreateDirectory(cache);
            async Task<string> Cache(string name)
            {
                var asset=release.Assets[name];var path=Path.Combine(cache,name);
                if(!File.Exists(path)||new FileInfo(path).Length!=asset.Size||Security.HashFile(path)!=asset.Sha256)await github.Download(asset,path,token);
                return path;
            }
            PackagePath=await Cache("vALERT-Agent-win-x64.zip");InstallerPath=await Cache("vALERT-Agent-Setup.exe");InstallerHash=Security.HashFile(InstallerPath);
            var unsigned=new UpdateManifest(release.Version,platform.Settings.PublicUrl.TrimEnd('/')+"/api/agents/package/"+release.Version,Security.HashFile(PackagePath),new FileInfo(PackagePath).Length,"");
            using var rsa=platform.SigningKey();Current=unsigned with {Signature=Convert.ToBase64String(rsa.SignData(Security.ManifestBytes(unsigned),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))};
            var console=Path.Combine(cache,"vALERT-Console-Setup.exe");var ca=release.Assets["vALERT-Console-Setup.exe"];
            if(File.Exists(console)&&new FileInfo(console).Length==ca.Size&&Security.HashFile(console)==ca.Sha256)ConsoleInstallerPath=console;
            Status="GitHub release "+release.Version+" detected";
        }
        catch(Exception e){Status="Release check failed: "+e.Message;throw;}
        finally{LastChecked=DateTimeOffset.UtcNow;gate.Release();}
    }
    public async Task DownloadConsole(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var release=Published??throw new ArgumentException("Check GitHub for releases first.");
            var destination=Path.Combine(store.DataDirectory,"release-cache",release.Version,"vALERT-Console-Setup.exe");
            await new GitHubReleases(http).Download(release.Assets["vALERT-Console-Setup.exe"],destination,token);
            ConsoleInstallerPath=destination;
        }
        finally{gate.Release();}
    }
}

public sealed class ConsoleUpdates(Store store)
{
    public static bool Held=>File.Exists(Path.Combine(AppContext.BaseDirectory,"update-hold"));
    public bool Available=>OperatingSystem.IsWindows()&&File.Exists(Path.Combine(AppContext.BaseDirectory,"console-install.json"));
    public ConsoleUpdateStatus? Status()
    {
        var request=Path.Combine(store.DataDirectory,"console-update-request.json");
        if(File.Exists(request)){try{var r=Wire.Decode<ConsoleUpdateRequest>(File.ReadAllText(request));var time=new DateTimeOffset(File.GetLastWriteTimeUtc(request));return DateTimeOffset.UtcNow-time>TimeSpan.FromMinutes(35)?new("Interrupted","The Windows updater has not picked up this request. Inspect the updater task and retry.",r.Version,time):new("Queued","The Windows updater will start within one minute. The console will briefly restart.",r.Version,time);}catch(IOException){} }
        var path=Path.Combine(store.DataDirectory,"console-update-status.json");
        try{
            var status=File.Exists(path)?Wire.Decode<ConsoleUpdateStatus>(File.ReadAllText(path)):null;
            return status?.Phase is "Downloading" or "Installing" && DateTimeOffset.UtcNow-status.Time>TimeSpan.FromMinutes(35)
                ?status with{Phase="Interrupted",Message="The updater stopped reporting. Retry the update or inspect the Windows updater task."}:status;
        }catch(IOException){return null;}
    }
    public void Queue(string version)
    {
        if(!Available)throw new ArgumentException("Install this release with the Windows console installer to enable in-portal updates.");
        if(!GitHubReleases.ValidVersion(version)||!Health.Newer(version,Wire.Version))throw new ArgumentException("A newer published console release is required.");
        if(Status()?.Phase is "Queued" or "Installing" or "Downloading")throw new ArgumentException("A console update is already in progress.");
        Security.AtomicWrite(Path.Combine(store.DataDirectory,"console-update-request.json"),Wire.Encode(new ConsoleUpdateRequest(Guid.NewGuid().ToString("N"),version)));
    }
}
