using System.Security.Cryptography;
using System.Text;
using Valert.Core;

namespace Valert.Agent;

public sealed record AgentSettings
{
    public string PortalUrl { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string CredentialProtected { get; set; } = "";
    public string UpdatePublicKey { get; set; } = "";
    public string VeeamUrl { get; set; } = "https://localhost:9419";
    public string VeeamUser { get; set; } = "";
    public string VeeamPasswordProtected { get; set; } = "";
    public string VeeamCertificateSha256 { get; set; } = "";
    public string OneUrl { get; set; } = "";
    public string OneUser { get; set; } = "";
    public string OnePasswordProtected { get; set; } = "";
    public string OneCertificateSha256 { get; set; } = "";
    public string OneApiVersion { get; set; } = "2.3";
    public bool UsePowerShell { get; set; }
    public string PowerShellPath { get; set; } = "";
    public bool AutomaticUpdates { get; set; } = true;
    public DateTimeOffset? LastHistoryScan { get; set; }
    public List<string> OpenSessionIds { get; set; } = [];
    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent credentials require Windows DPAPI.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine));
    }
    public static string Unprotect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent credentials require Windows DPAPI.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.LocalMachine));
    }
}

public sealed class DurableQueue(string directory)
{
    public string DirectoryPath { get; } = Path.Combine(directory,"queue");
    public int Count => Directory.Exists(DirectoryPath) ? Directory.GetFiles(DirectoryPath,"*.json").Length : 0;
    public void Add(AgentReport report)
    {
        // AtomicWrite flushes through to disk before committing the filename.
        Security.AtomicWrite(Path.Combine(DirectoryPath,report.CollectedAt.UtcTicks.ToString("D19") + "-" + report.ReportId + ".json"), Wire.Encode(report));
    }
    public IEnumerable<string> Files() => Directory.Exists(DirectoryPath) ? Directory.GetFiles(DirectoryPath,"*.json").OrderBy(p => p,StringComparer.Ordinal) : [];
    public void Acknowledge(string path, string sentId, AgentResponse response)
    {
        if (!response.Accepted || response.ReportId != sentId) throw new InvalidDataException("Portal did not acknowledge this report.");
        File.Delete(path);
    }
}
