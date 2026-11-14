using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Valert.Core;

public static class Security
{
    public static string Token(int bytes = 32) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static string Password(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32));
    }
    public static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            var p = encoded.Split(':');
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(p[1]), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[0]), 600_000, HashAlgorithmName.SHA256, 32));
        }
        catch (FormatException) { return false; }
        catch (IndexOutOfRangeException) { return false; }
    }
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string TotpSecret() => Base32(RandomNumberGenerator.GetBytes(20));
    public static string Base32(byte[] bytes)
    {
        var output = new StringBuilder(); int value = 0, bits = 0;
        foreach (var b in bytes) { value = (value << 8) | b; bits += 8; while (bits >= 5) { bits -= 5; output.Append(Alphabet[(value >> bits) & 31]); } }
        if (bits > 0) output.Append(Alphabet[(value << (5 - bits)) & 31]);
        return output.ToString();
    }
    public static byte[] FromBase32(string input)
    {
        var bytes = new List<byte>(); int value = 0, bits = 0;
        foreach (var c in input.TrimEnd('=').ToUpperInvariant()) { var index = Alphabet.IndexOf(c); if (index < 0) throw new FormatException(); value = (value << 5) | index; bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)(value >> bits)); } }
        return bytes.ToArray();
    }
    public static string Totp(string secret, long step, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var h = HMACSHA1.HashData(FromBase32(secret), counter); var o = h[^1] & 15;
        var n = (BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(o, 4)) & 0x7fffffff) % (int)Math.Pow(10, digits);
        return n.ToString(new string('0', digits));
    }
    public static long? VerifyTotp(string secret, string code, DateTimeOffset now, long lastUsed = -1)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var step = now.ToUnixTimeSeconds() / 30;
        for (var i = -1; i <= 1; i++) if (step + i > lastUsed && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Totp(secret, step + i)), Encoding.ASCII.GetBytes(code))) return step + i;
        return null;
    }
    public static byte[] EncryptArchive(byte[] plain, string password)
    {
        if (password.Length < 16) throw new ArgumentException("Use a migration passphrase of at least 16 characters.");
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, "VALERT1"u8);
        CryptographicOperations.ZeroMemory(key);
        return [.. "VALERT1"u8.ToArray(), .. salt, .. nonce, .. tag, .. cipher];
    }
    public static byte[] DecryptArchive(byte[] data, string password)
    {
        if (data.Length < 51 || !data.AsSpan(0,7).SequenceEqual("VALERT1"u8)) throw new InvalidDataException("Not a vALERT migration archive.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, data.AsSpan(7,16), 600_000, HashAlgorithmName.SHA256, 32); var plain = new byte[data.Length - 51];
        using var aes = new AesGcm(key, 16); aes.Decrypt(data.AsSpan(23,12), data.AsSpan(51), data.AsSpan(35,16), plain, "VALERT1"u8); CryptographicOperations.ZeroMemory(key); return plain;
    }
    public static byte[] ManifestBytes(UpdateManifest m) => Encoding.UTF8.GetBytes($"{m.Version}\n{m.Url}\n{m.Sha256.ToLowerInvariant()}\n{m.Size}\n");
    public static bool VerifyManifest(UpdateManifest m, string publicKey)
    {
        try { using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey); return rsa.VerifyData(ManifestBytes(m), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { var bytes = Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
