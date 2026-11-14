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
                    // A pooled SQLite handle keeps the snapshot locked on Windows after Dispose.
                    using (var source = store.Open()) using (var target = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString())) { target.Open(); source.BackupDatabase(target); }
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

