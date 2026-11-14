using System.Security.Authentication;

namespace Valert.Core;

/// <summary>Transport for agent enrollment, telemetry, and console-hosted updates.</summary>
public static class ConsoleTransport
{
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    public static Uri Origin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The console address must be an HTTPS origin without credentials, a path, query, or fragment.");
        return uri;
    }

    public static HttpClient CreateClient(string url, TimeSpan? timeout = null)
    {
        var origin = Origin(url);
        // Retain platform chain, hostname, and expiry validation. No certificate bypass or pin override.
        var handler = new HttpClientHandler { AllowAutoRedirect = false, SslProtocols = Protocols };
        return new HttpClient(new OriginGuard(origin, handler)) { BaseAddress = origin, Timeout = timeout ?? TimeSpan.FromMinutes(2) };
    }

    sealed class OriginGuard(Uri origin, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri;
            if (uri is null || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                !string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Agent traffic must use the enrolled console's HTTPS origin.");
            return base.SendAsync(request, token);
        }
    }
}
