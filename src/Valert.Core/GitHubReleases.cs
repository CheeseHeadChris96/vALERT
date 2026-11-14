using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valert.Core;

public sealed record PublishedRelease(string Version, DateTimeOffset PublishedAt, string PageUrl, Dictionary<string,PublishedAsset> Assets);
public sealed record PublishedAsset(string Name, string Url, long Size, string Sha256);
public sealed class GitHubReleases(HttpClient http)
{
    public const string Repository = "CheeseHeadChris96/vALERT";
    public static bool ValidVersion(string value) => value.Length <= 80 && Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)?$");
    public async Task<PublishedRelease?> Latest(bool previews, CancellationToken token)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=20", token);
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var releases = json.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean() && (previews || !r.GetProperty("prerelease").GetBoolean())).ToList();
        JsonElement? latest = null; string version = "0.0.0";
        foreach (var r in releases) { var candidate = r.GetProperty("tag_name").GetString()!.TrimStart('v'); if (ValidVersion(candidate) && (latest is null || Health.Newer(candidate,version))) { latest = r; version = candidate; } }
        return latest is { } release ? await Read(release, token) : null;
    }
    public async Task<PublishedRelease> Version(string version, CancellationToken token)
    {
        if (!ValidVersion(version)) throw new InvalidDataException("Invalid release version.");
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/tags/v{version}", token);
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.GetProperty("draft").GetBoolean() || json.RootElement.GetProperty("tag_name").GetString() != "v"+version) throw new InvalidDataException("Release is not published.");
        return await Read(json.RootElement, token);
    }
    async Task<PublishedRelease> Read(JsonElement release, CancellationToken token)
    {
        var version = release.GetProperty("tag_name").GetString()!.TrimStart('v');
        if (!ValidVersion(version)) throw new InvalidDataException("Invalid release version.");
        var files = release.GetProperty("assets").EnumerateArray().ToDictionary(a=>a.GetProperty("name").GetString()!);
        string Url(string name)
        {
            if (!files.TryGetValue(name,out var asset)) throw new InvalidDataException("Release asset is missing: "+name);
            var url=asset.GetProperty("browser_download_url").GetString()!;
            if (url != $"https://github.com/{Repository}/releases/download/v{version}/{name}") throw new InvalidDataException("Unexpected release asset origin.");
            return url;
        }
        var manifestText = await http.GetStringAsync(Url("release-manifest.json"),token);
        if (manifestText.Length > 65536) throw new InvalidDataException("Release manifest is too large.");
        var manifest = Wire.Decode<Dictionary<string,ManifestFile>>(manifestText); var assets = new Dictionary<string,PublishedAsset>();
        foreach (var name in new[]{"vALERT-Agent-Setup.exe","vALERT-Agent-win-x64.zip","vALERT-Console-Setup.exe","vALERT-Portal-win-x64.zip"})
        {
            if (!manifest.TryGetValue(name,out var checksum) || checksum.Size is <= 0 or > 500_000_000 || !Regex.IsMatch(checksum.Sha256,@"^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid release checksum metadata.");
            var url = Url(name); var asset = files[name];
            if (asset.GetProperty("size").GetInt64() != checksum.Size) throw new InvalidDataException("Release size does not match manifest.");
            if (asset.TryGetProperty("digest",out var digest) && digest.ValueKind == JsonValueKind.String && digest.GetString() is { Length: > 0 } hash && !hash.Equals("sha256:"+checksum.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub asset digest does not match manifest.");
            assets[name] = new(name,url,checksum.Size,checksum.Sha256.ToLowerInvariant());
        }
        return new(version,release.GetProperty("published_at").GetDateTimeOffset(),$"https://github.com/{Repository}/releases/tag/v{version}",assets);
    }
    public async Task Download(PublishedAsset asset, string destination, CancellationToken token)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".download"; Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using var response=await http.GetAsync(asset.Url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            {
                var buffer=new byte[81920];long total=0;int n;
                while((n=await input.ReadAsync(buffer,token))>0){total+=n;if(total>asset.Size)throw new InvalidDataException("Download exceeded the release size.");await output.WriteAsync(buffer.AsMemory(0,n),token);}
                await output.FlushAsync(token);output.Flush(true);
            }
            if(new FileInfo(temp).Length!=asset.Size||Security.HashFile(temp)!=asset.Sha256)throw new InvalidDataException("Downloaded release failed SHA-256 validation.");
            File.Move(temp,destination,true);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    sealed record ManifestFile(string Sha256,long Size);
}
