using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StbImageSharp;
using Valert.Core;
using Valert.Portal;
using Xunit;
using ZXing;

namespace Valert.Tests;

public sealed class MfaEnrollmentTests
{
    public static string DecodeQr(string dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, dataUrl);
        var image = ImageResult.FromMemory(Convert.FromBase64String(dataUrl[prefix.Length..]), ColorComponents.RedGreenBlueAlpha);
        var result = new BarcodeReaderGeneric().Decode(image.Data, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.QR_CODE, result.BarcodeFormat);
        return result.Text;
    }

    [Theory]
    [InlineData("admin@example.test")]
    [InlineData("team+alerts@example.test")]
    [InlineData("élise@example.test")]
    public void QrImageDecodesToTheAccountsAuthenticatorSettings(string email)
    {
        var secret = Security.TotpSecret(); var setup = MfaEnrollment.Create(email, secret);
        var uri = new Uri(DecodeQr(setup.QrCodeDataUrl));
        Assert.Equal(setup.Uri, uri.AbsoluteUri);
        Assert.Equal("otpauth", uri.Scheme); Assert.Equal("totp", uri.Host);
        Assert.Equal("/vALERT:" + email, Uri.UnescapeDataString(uri.AbsolutePath));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(secret, query["secret"]); Assert.Equal("vALERT", query["issuer"]);
        Assert.Equal("6", query["digits"]); Assert.Equal("30", query["period"]); Assert.Equal("SHA1", query["algorithm"]);
    }

    [Fact]
    public async Task InitialSetupQrMustBeConfirmedAndIsNeverReturnedForNormalLogin()
    {
        using var f = new PortalFactory(); using var client = f.Browser(); await Refresh(client);
        using var response = await client.PostAsJsonAsync("/api/auth/setup", new { token = File.ReadAllText(Path.Combine(f.DataPath,"initial-setup.txt")), name="QR administrator", email="admin@example.test", password="strong qr test password" });
        var setup = await Json(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("enroll", setup.GetProperty("stage").GetString());
        var uri = DecodeQr(setup.GetProperty("qrCodeDataUrl").GetString()!);
        Assert.Equal(setup.GetProperty("uri").GetString(), uri);
        var secret = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(uri).Query)["secret"].ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/mfa", new { code="invalid" })).StatusCode);
        Assert.False(f.Services.GetRequiredService<Store>().Users().Single().MfaConfirmed);
        var retry = await Json(await client.PostAsJsonAsync("/api/auth/login", new { email="admin@example.test", password="strong qr test password" }));
        Assert.Equal(uri, DecodeQr(retry.GetProperty("qrCodeDataUrl").GetString()!));
        var confirmed = await Json(await client.PostAsJsonAsync("/api/auth/mfa", new { code=Security.Totp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30) }));
        Assert.Equal(10, confirmed.GetProperty("recoveryCodes").GetArrayLength());
        Assert.False(confirmed.TryGetProperty("qrCodeDataUrl", out _));
        await Refresh(client); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/overview")).StatusCode);
        (await client.PostAsJsonAsync("/api/auth/logout", new { })).EnsureSuccessStatusCode(); await Refresh(client);
        var login = await Json(await client.PostAsJsonAsync("/api/auth/login", new { email="admin@example.test", password="strong qr test password" }));
        Assert.Equal("mfa", login.GetProperty("stage").GetString());
        foreach (var property in new[] { "secret", "uri", "qrCodeDataUrl" }) Assert.Equal(JsonValueKind.Null, login.GetProperty(property).ValueKind);
    }

    [Fact]
    public async Task InvitedAccountsGetDifferentRandomQrSecretsAndAccountRecoveryReplacesTheSecret()
    {
        using var f = new PortalFactory(); using var firstClient = f.Browser(); await Refresh(firstClient);
        var store = f.Services.GetRequiredService<Store>();
        PortalUser Invite(string email, string token)
        {
            var user = new PortalUser { Name="QR test user", Email=email, ActivationHash=Security.Hash(token), ActivationExpires=DateTimeOffset.UtcNow.AddHours(1) };
            store.Save(user); return user;
        }
        var first = Invite("first@example.test", "synthetic-first-invitation");
        var a = await Json(await firstClient.PostAsJsonAsync("/api/auth/activate", new { token="synthetic-first-invitation", password="strong qr test password" }));
        using var secondClient = f.Browser(); await Refresh(secondClient); Invite("second@example.test", "synthetic-second-invitation");
        var b = await Json(await secondClient.PostAsJsonAsync("/api/auth/activate", new { token="synthetic-second-invitation", password="strong qr test password" }));
        Assert.NotEqual(a.GetProperty("secret").GetString(), b.GetProperty("secret").GetString());
        Assert.NotEqual(DecodeQr(a.GetProperty("qrCodeDataUrl").GetString()!), DecodeQr(b.GetProperty("qrCodeDataUrl").GetString()!));
        Assert.DoesNotContain(a.GetProperty("secret").GetString()!, store.User(first.Id)!.TotpProtected);
        // Match the reset state written by the administrator recovery endpoint.
        var reset = store.User(first.Id)!; reset.TotpProtected=""; reset.Stamp=Security.Token(); reset.ActivationHash=Security.Hash("synthetic-reset"); reset.ActivationExpires=DateTimeOffset.UtcNow.AddHours(1); store.Save(reset);
        var again = await Json(await firstClient.PostAsJsonAsync("/api/auth/activate", new { token="synthetic-reset", password="strong qr test password" }));
        Assert.NotEqual(a.GetProperty("secret").GetString(), again.GetProperty("secret").GetString());
        Assert.Equal(again.GetProperty("uri").GetString(), DecodeQr(again.GetProperty("qrCodeDataUrl").GetString()!));
    }

    static async Task<JsonElement> Json(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone(); }
    static async Task Refresh(HttpClient client)
    {
        var session=await Json(await client.GetAsync("/api/auth/session"));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrf").GetString());
    }
}
