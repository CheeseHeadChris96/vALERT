using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Valert.Core;
using Valert.Portal;
using Xunit;

namespace Valert.Tests;

public sealed class TransportTests
{
    [Fact]
    public void CustomerPackageContainsHttpsOriginAndExecutableBootstrapResource()
    {
        var path = Path.Combine(Path.GetTempPath(), "valert-package-" + Guid.NewGuid());
        try
        {
            var store = new Store(path); var customer = new Customer { Name = "TLS test" }; store.Save(customer);
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(new Platform(store).EnrollmentPackage(customer)));
            using var enrollment = new StreamReader(zip.GetEntry("enrollment.json")!.Open());
            Assert.Equal("https://valert.rebootranch.net", Wire.Decode<Enrollment>(enrollment.ReadToEnd()).PortalUrl);
            using var bootstrap = new StreamReader(zip.GetEntry("Install-vALERT.ps1")!.Open());
            var script = bootstrap.ReadToEnd();
            Assert.Contains("$portal = Assert-ConsoleOrigin $e.portalUrl", script);
            Assert.Contains("$handler.AllowAutoRedirect = $false", script);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData("http://valert.rebootranch.net")]
    [InlineData("https://user:password@valert.rebootranch.net")]
    [InlineData("https://valert.rebootranch.net/path")]
    [InlineData("https://valert.rebootranch.net?token=secret")]
    [InlineData("https://valert.rebootranch.net#fragment")]
    public void UnsafeEnrollmentOriginsAreRejected(string url) => Assert.Throws<ArgumentException>(() => ConsoleTransport.CreateClient(url));

    [Theory]
    [InlineData("http://valert.rebootranch.net/api/agents/reports")]
    [InlineData("https://another.example/api/agents/reports")]
    [InlineData("https://valert.rebootranch.net:8443/api/agents/reports")]
    public async Task CredentialsCannotBeSentOutsideEnrolledHttpsOrigin(string url)
    {
        using var client = ConsoleTransport.CreateClient("https://valert.rebootranch.net");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-test-credential");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(url, new StringContent("synthetic report")));
    }

    [Theory]
    [InlineData("/api/agents/enroll")]
    [InlineData("/api/agents/reports")]
    [InlineData("/api/agents/package/0.1.0")]
    [InlineData("/api/enrollment/installer")]
    public async Task AgentEndpointsRefuseHttpEvenOnLocalSetupListener(string path)
    {
        using var factory = new PortalFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.PostAsync(path, new StringContent("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Agent traffic requires HTTPS", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void DeploymentHostnameIsDefaultAndSavedConfigurationIsPreserved()
    {
        Assert.Equal("https://valert.rebootranch.net", new PortalSettings().PublicUrl);
        var path = Path.Combine(Path.GetTempPath(), "valert-origin-" + Guid.NewGuid());
        try
        {
            Security.AtomicWrite(Path.Combine(path,"portal-settings.json"), Wire.Encode(new PortalSettings { PublicUrl="https://existing.example" }));
            Assert.Equal("https://existing.example", Platform.LoadSettings(path).PublicUrl);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task UntrustedCertificateNeverReceivesAgentCredentialOrReport()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await using var peer = new TlsPeer(certificate);
        using var client = ConsoleTransport.CreateClient(peer.Origin);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "must-not-reach-server");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("api/agents/reports", new StringContent("private report")));
        await peer.Completion;
        Assert.Null(peer.HttpRequest);
    }

    [Fact]
    public async Task WindowsTrustedTlsRejectsWrongHostExpiredCertificatesAndDowngradeRedirects()
    {
        // Disposable elevated Windows CI only; CurrentUser\Root can show an interactive trust dialog.
        // The temporary machine-store CA is removed in finally. Never change a developer's trust store.
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=vALERT CI TLS " + Guid.NewGuid(), caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(2));
        using var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine); roots.Open(OpenFlags.ReadWrite);
        roots.Add(ca);
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var publicCert = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            using var certificate = publicCert.CopyWithPrivateKey(key);
            await using (var peer = new TlsPeer(certificate))
            {
                using var client = ConsoleTransport.CreateClient(peer.Origin);
                client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-tls-credential");
                using var response = await client.PostAsync("api/agents/reports", new StringContent("synthetic report"));
                response.EnsureSuccessStatusCode(); await peer.Completion;
                Assert.True(peer.Encrypted); Assert.Equal(SslProtocols.Tls12, peer.Protocol);
                Assert.Contains("Authorization: Bearer synthetic-tls-credential", peer.HttpRequest);
                Assert.Contains("synthetic report", peer.HttpRequest);
            }
            await using (var peer = new TlsPeer(certificate, "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/leak\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
            {
                using var client = ConsoleTransport.CreateClient(peer.Origin);
                using var response = await client.GetAsync("api/agents/package/version");
                Assert.Equal(HttpStatusCode.Found, response.StatusCode); await peer.Completion;
            }
            await using (var peer = new TlsPeer(certificate))
            {
                using var client = ConsoleTransport.CreateClient(peer.Origin.Replace("localhost", "127.0.0.1"));
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/agents/reports"));
                await peer.Completion; Assert.Null(peer.HttpRequest);
            }
            using var expiredPublic = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1), RandomNumberGenerator.GetBytes(16));
            using var expired = expiredPublic.CopyWithPrivateKey(key);
            await using (var peer = new TlsPeer(expired))
            {
                using var client = ConsoleTransport.CreateClient(peer.Origin);
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/agents/reports"));
                await peer.Completion; Assert.Null(peer.HttpRequest);
            }
        }
        finally { roots.Remove(ca); }
    }

    sealed class TlsPeer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        public string Origin { get; }
        public Task Completion { get; }
        public string? HttpRequest { get; private set; }
        public bool Encrypted { get; private set; }
        public SslProtocols Protocol { get; private set; }
        public TlsPeer(X509Certificate2 certificate, string response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK")
        {
            listener.Start(); Origin = "https://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Completion = Serve(certificate, response);
        }
        async Task Serve(X509Certificate2 certificate, string response)
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, timeout.Token);
                Encrypted = tls.IsEncrypted && tls.IsSigned; Protocol = tls.SslProtocol;
                using var reader = new StreamReader(tls, Encoding.UTF8, leaveOpen: true);
                var header = new StringBuilder(); var length = 0;
                while (await reader.ReadLineAsync(timeout.Token) is { } line && line.Length > 0)
                {
                    header.AppendLine(line);
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
                }
                if (header.Length == 0) return;
                var body = new char[length]; await reader.ReadBlockAsync(body, timeout.Token);
                HttpRequest = header + new string(body);
                await tls.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }
            catch (Exception e) when (e is AuthenticationException or IOException) { /* Expected for rejected certificates. */ }
        }
        public async ValueTask DisposeAsync() { timeout.Cancel(); listener.Stop(); try { await Completion; } catch (OperationCanceledException) { } timeout.Dispose(); }
    }
}
