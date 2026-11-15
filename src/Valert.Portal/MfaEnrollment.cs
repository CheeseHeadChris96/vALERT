using QRCoder;

namespace Valert.Portal;

public sealed record MfaEnrollment(string Uri, string QrCodeDataUrl)
{
    public static MfaEnrollment Create(string email, string secret)
    {
        var uri = $"otpauth://totp/vALERT:{System.Uri.EscapeDataString(email)}?secret={System.Uri.EscapeDataString(secret)}&issuer=vALERT&algorithm=SHA1&digits=6&period=30";
        // Encode locally: the authenticator secret must never reach an external QR service.
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return new(uri, "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(8)));
    }
}
