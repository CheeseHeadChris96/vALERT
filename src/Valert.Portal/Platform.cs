using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valert.Core;

namespace Valert.Portal;

public sealed class Platform(Store store)
{
    public PortalSettings Settings { get; private set; } = LoadSettings(store.DataDirectory);
    public string PublicKey
    {
        get { using var rsa = SigningKey(); return rsa.ExportSubjectPublicKeyInfoPem(); }
    }
    public static PortalSettings LoadSettings(string directory)
    {
        var path = Path.Combine(directory, "portal-settings.json");
        return File.Exists(path) ? Wire.Decode<PortalSettings>(File.ReadAllText(path)) : new PortalSettings();
    }
    public void SaveSettings() => Security.AtomicWrite(Path.Combine(store.DataDirectory, "portal-settings.json"), Wire.Encode(Settings));
    public RSA SigningKey()
    {
        lock (store.Gate)
        {
            var path = Path.Combine(store.DataDirectory, "update-signing.pem"); var rsa = RSA.Create(3072);
            if (File.Exists(path)) rsa.ImportFromPem(File.ReadAllText(path)); else Security.AtomicWrite(path, rsa.ExportPkcs8PrivateKeyPem());
            return rsa;
        }
    }
    public byte[] EnrollmentPackage(Customer customer)
    {
        if (!Uri.TryCreate(Settings.PublicUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host == "localhost") throw new ArgumentException("Configure your public HTTPS hostname in Settings before enrolling a customer.");
        var token = Security.Token(); var expiry = DateTimeOffset.UtcNow.AddHours(24);
        store.Exec("INSERT INTO enrollments VALUES(@p0,@p1,@p2)", Security.Hash(token), customer.Id, expiry.ToString("O"));
        var enrollment = new Enrollment(Settings.PublicUrl.TrimEnd('/'), token, customer.Name, PublicKey, expiry);
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Add("enrollment.json", Wire.Encode(enrollment));
            Add("Install-vALERT.ps1", """
                # Run this customer-specific bootstrap from an elevated PowerShell window.
                # Downloads the approved installer over HTTPS and checks its portal-supplied checksum.
                $ErrorActionPreference = 'Stop'
                $e = Get-Content (Join-Path $PSScriptRoot 'enrollment.json') -Raw | ConvertFrom-Json
                if ([DateTimeOffset]::Parse($e.expiresAt) -lt [DateTimeOffset]::UtcNow) { throw 'Enrollment expired. Generate a new package in vALERT.' }
                $root = $e.portalUrl.TrimEnd('/')
                $request = @{token=$e.token} | ConvertTo-Json
                $asset = Invoke-RestMethod -Method Post -Uri "$root/api/enrollment/installer" -ContentType 'application/json' -Body $request
                $file = Join-Path $env:TEMP ('vALERT-Agent-' + [guid]::NewGuid().ToString('N') + '.exe')
                Invoke-WebRequest -Uri $asset.url -OutFile $file -UseBasicParsing
                if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $asset.sha256) { Remove-Item $file; throw 'Installer checksum mismatch.' }
                # The enrollment endpoint is authenticated by the single-use token over verified TLS.
                # Bootstrap never disables certificate validation and never contacts Veeam itself.
                $process = Start-Process -FilePath $file -ArgumentList @('/ENROLLMENT="' + (Join-Path $PSScriptRoot 'enrollment.json') + '"') -Wait -PassThru
                if ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode)." }
                Remove-Item $file -Force
                Write-Host 'Installation complete. Check the customer page for enrollment and collection status.'
                """);
            Add("README.txt", $"vALERT enrollment for {customer.Name}\r\nExpires: {expiry:O}\r\nExtract this ZIP, then run Install-vALERT.ps1 as Administrator.\r\nSetup requests a dedicated Veeam monitoring identity. Do not share this package.\r\nThe token can enroll one server only. Generate another package for additional servers.\r\n");
        }
        return output.ToArray();
    }
    public byte[] Export(string password)
    {
        lock (store.Gate)
        {
            Settings.MigrationPaused = true; SaveSettings();
            using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var temp = Path.Combine(store.DataDirectory, "migration-" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    using (var source = store.Open()) using (var target = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + temp)) { target.Open(); source.BackupDatabase(target); }
                    zip.CreateEntryFromFile(temp, "valert.db", CompressionLevel.Fastest);
                }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(temp)) File.Delete(temp); }
                foreach (var file in new[] { "portal-settings.json", "update-signing.pem" }.Where(f => File.Exists(Path.Combine(store.DataDirectory,f)))) zip.CreateEntryFromFile(Path.Combine(store.DataDirectory,file), file);
                var keys = Path.Combine(store.DataDirectory, "keys");
                if (Directory.Exists(keys)) foreach (var file in Directory.GetFiles(keys, "*.xml")) zip.CreateEntryFromFile(file, "keys/" + Path.GetFileName(file));
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(Wire.Encode(new { schema = 1, version = Wire.Version, createdAt = DateTimeOffset.UtcNow, customers = store.Customers().Count, agents = store.Agents().Count, users = store.Users().Count }));
            }
            return Security.EncryptArchive(output.ToArray(), password);
        }
    }
    public static void Restore(string directory, string archive, string password)
    {
        if (File.Exists(Path.Combine(directory, "valert.db"))) throw new InvalidOperationException("Restore requires an empty destination data directory. Stop the service and choose a new data directory.");
        var plain = Security.DecryptArchive(File.ReadAllBytes(archive), password);
        using var zip = new ZipArchive(new MemoryStream(plain));
        var allowed = new HashSet<string> { "valert.db", "portal-settings.json", "update-signing.pem", "manifest.json" };
        if (zip.GetEntry("valert.db") is null || zip.GetEntry("manifest.json") is null) throw new InvalidDataException("Incomplete archive.");
        foreach (var e in zip.Entries)
            if (!allowed.Contains(e.FullName) && !(e.FullName.StartsWith("keys/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal) && e.FullName.Split('/').Length == 2 && !e.FullName.Contains(".."))) throw new InvalidDataException("Unexpected archive path.");
        if (zip.Entries.Sum(e => e.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive exceeds 2 GB restore limit.");
        Directory.CreateDirectory(directory);
        foreach (var e in zip.Entries) { var path = Path.Combine(directory, e.FullName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); e.ExtractToFile(path, false); }
        var settings = LoadSettings(directory); settings.MigrationPaused = false; Security.AtomicWrite(Path.Combine(directory, "portal-settings.json"), Wire.Encode(settings));
        var restored = new Store(directory); foreach (var user in restored.Users()) { user.Stamp = Security.Token(); restored.Save(user); }
        restored.Audit("local recovery", "Portal restored; browser sessions invalidated", Wire.Version);
    }
}

public sealed class Releases(Store store, Platform platform, ILogger<Releases> logger) : BackgroundService
{
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
    public UpdateManifest? Current { get; private set; }
    public string? InstallerPath { get; private set; }
    public string? InstallerHash { get; private set; }
    public string Status { get; private set; } = "Checking published releases";
    public string? PackagePath { get; private set; }
    readonly SemaphoreSlim refresh = new(1,1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("vALERT/" + Wire.Version);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Refresh(stoppingToken); if (!platform.Settings.MigrationPaused) store.Retain(DateTimeOffset.UtcNow); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { Status = "Release check failed: " + e.Message; logger.LogWarning("Release check failed: {Reason}", e.Message); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Refresh(CancellationToken cancellationToken)
    {
        await refresh.WaitAsync(cancellationToken);
        try
        {
            var text = await http.GetStringAsync("https://api.github.com/repos/CheeseHeadChris96/vALERT/releases?per_page=20", cancellationToken);
            var releases = JsonDocument.Parse(text).RootElement;
            var release = releases.EnumerateArray().FirstOrDefault(r => !r.GetProperty("draft").GetBoolean() && (platform.Settings.IncludePreviewUpdates || !r.GetProperty("prerelease").GetBoolean()));
            if (release.ValueKind == JsonValueKind.Undefined) { Status = "No published release yet"; return; }
            var version = release.GetProperty("tag_name").GetString()!.TrimStart('v');
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            JsonElement Asset(string name) => assets.FirstOrDefault(a => a.GetProperty("name").GetString() == name);
            var manifest = Asset("release-manifest.json"); if (manifest.ValueKind == JsonValueKind.Undefined) { Status = "Release has no verified package manifest"; return; }
            var checks = Wire.Decode<Dictionary<string,ReleaseFile>>(await http.GetStringAsync(manifest.GetProperty("browser_download_url").GetString(), cancellationToken));
            var cache = Path.Combine(store.DataDirectory, "release-cache", version);
            if (version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains("..") || version.Contains('/')) throw new InvalidDataException("Invalid release version.");
            Directory.CreateDirectory(cache);
            async Task<string> Download(string name)
            {
                var asset = Asset(name); if (asset.ValueKind == JsonValueKind.Undefined || !checks.TryGetValue(name, out var check)) throw new InvalidDataException("Incomplete release: " + name);
                var path = Path.Combine(cache, name);
                if (!File.Exists(path) || Security.HashFile(path) != check.Sha256.ToLowerInvariant())
                {
                    var temp = path + ".download"; using var response = await http.GetAsync(asset.GetProperty("browser_download_url").GetString(), HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode();
                    if (check.Size > 500_000_000) throw new InvalidDataException("Package exceeds limit.");
                    await using (var target = File.Create(temp)) await response.Content.CopyToAsync(target, cancellationToken);
                    if (new FileInfo(temp).Length != check.Size || Security.HashFile(temp) != check.Sha256.ToLowerInvariant()) { File.Delete(temp); throw new InvalidDataException("Release checksum mismatch."); }
                    File.Move(temp, path, true);
                }
                return path;
            }
            PackagePath = await Download("vALERT-Agent-win-x64.zip"); InstallerPath = await Download("vALERT-Agent-Setup.exe"); InstallerHash = Security.HashFile(InstallerPath);
            var unsigned = new UpdateManifest(version, platform.Settings.PublicUrl.TrimEnd('/') + "/api/agents/package/" + Uri.EscapeDataString(version), Security.HashFile(PackagePath), new FileInfo(PackagePath).Length, "");
            using var rsa = platform.SigningKey(); Current = unsigned with { Signature = Convert.ToBase64String(rsa.SignData(Security.ManifestBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) }; Status = "Release " + version + " ready";
        }
        finally { refresh.Release(); }
    }
    sealed record ReleaseFile(string Sha256, long Size);
}
