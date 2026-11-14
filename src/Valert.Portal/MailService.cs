using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using MimeKit;

namespace Valert.Portal;

public sealed record SmtpRequest(bool Enabled, string Host, int Port, string Security, string Username, string? Password, bool ClearPassword, string FromAddress, string FromName);
public interface IMailTransport
{
    Task Send(SmtpSettings settings, string password, MimeMessage message, CancellationToken cancellationToken);
}
public sealed class SmtpTransport : IMailTransport
{
    public async Task Send(SmtpSettings settings, string password, MimeMessage message, CancellationToken cancellationToken)
    {
        using var smtp = new SmtpClient { Timeout = 30000 };
        var security = settings.Security == "SslOnConnect" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await smtp.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
        if (settings.Username.Length > 0) await smtp.AuthenticateAsync(settings.Username, password, cancellationToken);
        await smtp.SendAsync(message, cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }
}
public sealed class MailService(Platform platform, IDataProtectionProvider protection, IMailTransport transport)
{
    readonly IDataProtector protector = protection.CreateProtector("vALERT.SmtpSecret.v1");
    public void Configure(SmtpRequest input)
    {
        if (input.Port is < 1 or > 65535 || input.Security is not ("StartTls" or "SslOnConnect")) throw new ArgumentException("Choose a valid SMTP port and TLS mode.");
        var host = input.Host.Trim(); var username = input.Username.Trim(); var from = input.FromAddress.Trim();
        if (host.Length > 253 || (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)) throw new ArgumentException("Enter an SMTP hostname or IP address without a URL or port.");
        if (username.Length > 256 || input.FromName.Length > 150 || input.FromName.Contains('\r') || input.FromName.Contains('\n')) throw new ArgumentException("Invalid SMTP account or sender name.");
        if (from.Length > 0 && (!System.Net.Mail.MailAddress.TryCreate(from, out var address) || address.Address != from)) throw new ArgumentException("Enter a sender email address.");
        var password = input.ClearPassword ? "" : platform.Settings.Smtp.PasswordProtected;
        if (!string.IsNullOrEmpty(input.Password)) password = protector.Protect(input.Password);
        // Never reuse a saved credential with a different destination or account.
        if ((host != platform.Settings.Smtp.Host || input.Port != platform.Settings.Smtp.Port || username != platform.Settings.Smtp.Username) && string.IsNullOrEmpty(input.Password)) password = "";
        if (input.Enabled && (host.Length == 0 || from.Length == 0 || (username.Length > 0 && password.Length == 0))) throw new ArgumentException("Set a host and sender, and a password when authentication is used, before enabling SMTP.");
        platform.Settings.Smtp = new SmtpSettings { Enabled = input.Enabled, Host = host, Port = input.Port, Security = input.Security, Username = username, PasswordProtected = password, FromAddress = from, FromName = input.FromName.Trim() };
        platform.SaveSettings();
    }
    public async Task SendTest(string recipient, CancellationToken cancellationToken)
    {
        var settings = platform.Settings.Smtp with { };
        if (!settings.Enabled) throw new ArgumentException("SMTP is disabled. Save and enable your SMTP settings before sending a test.");
        if (!System.Net.Mail.MailAddress.TryCreate(recipient, out var address) || address.Address != recipient) throw new ArgumentException("Enter one test recipient email address.");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(new MailboxAddress("", recipient));
        message.Subject = "vALERT SMTP test";
        message.Body = new TextPart("plain") { Text = "This test was requested by a vALERT administrator. SMTP delivery is working. No backup notification rules have been enabled." };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await transport.Send(settings, settings.PasswordProtected.Length == 0 ? "" : protector.Unprotect(settings.PasswordProtected), message, timeout.Token);
    }
}
