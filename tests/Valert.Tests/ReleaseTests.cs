using System.Net;
using System.Security.Cryptography;
using Valert.Core;
using Xunit;

namespace Valert.Tests;
public sealed class ReleaseTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ReleaseMetadataMustComeFromTheFixedRepository(bool substituteUrl)
    {
        const string version="0.2.0";var hash=new string('a',64);var names=new[]{"vALERT-Agent-Setup.exe","vALERT-Agent-win-x64.zip","vALERT-Console-Setup.exe","vALERT-Portal-win-x64.zip"};
        var manifest=names.ToDictionary(n=>n,n=>new{sha256=hash,size=4});
        var assets=names.Append("release-manifest.json").Select(n=>new{name=n,size=4,browser_download_url=(substituteUrl?"https://attacker.example/":$"https://github.com/{GitHubReleases.Repository}/releases/download/v{version}/")+n,digest="sha256:"+hash});
        var metadata=Wire.Encode(new{draft=false,prerelease=false,tag_name="v"+version,published_at=DateTimeOffset.UtcNow,assets});
        using var http=new HttpClient(new Handler(request=>request.RequestUri!.AbsolutePath.EndsWith("release-manifest.json")?Wire.Encode(manifest):metadata));
        var releases=new GitHubReleases(http);
        if(substituteUrl)await Assert.ThrowsAsync<InvalidDataException>(()=>releases.Version(version,CancellationToken.None));else Assert.Equal(version,(await releases.Version(version,CancellationToken.None)).Version);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DownloadRequiresMatchingLengthAndChecksum(bool tamper)
    {
        var directory=Path.Combine(Path.GetTempPath(),"valert-release-test-"+Guid.NewGuid());var file=Path.Combine(directory,"asset.zip");
        using var http=new HttpClient(new Handler(_=>tamper?"evil":"good"));var digest=Convert.ToHexString(SHA256.HashData("good"u8)).ToLowerInvariant();
        try{var download=new GitHubReleases(http).Download(new("asset.zip","https://github.com/file",4,digest),file,CancellationToken.None);if(tamper){await Assert.ThrowsAsync<InvalidDataException>(()=>download);Assert.False(File.Exists(file));}else{await download;Assert.Equal("good",File.ReadAllText(file));}}finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    sealed class Handler(Func<HttpRequestMessage,string> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response(request))});}
}
