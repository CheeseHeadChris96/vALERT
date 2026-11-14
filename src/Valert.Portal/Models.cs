using Valert.Core;

namespace Valert.Portal;

public sealed record PortalSettings
{
    public string PublicUrl { get; set; } = "https://localhost";
    public string CertificateThumbprint { get; set; } = "";
    public int HttpsPort { get; set; } = 443;
    public int LocalPort { get; set; } = 5080;
    public bool IncludePreviewUpdates { get; set; } = true;
    public bool AutomaticUpdateChecks { get; set; } = true;
    public SmtpSettings Smtp { get; set; } = new();
    public bool MigrationPaused { get; set; }
    public object Safe() => new { PublicUrl, CertificateThumbprint, HttpsPort, LocalPort, IncludePreviewUpdates, AutomaticUpdateChecks, MigrationPaused, Smtp = Smtp.Safe() };
}
public sealed record SmtpSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "vALERT";
    public object Safe() => new { Enabled, Host, Port, Security, Username, FromAddress, FromName, PasswordConfigured = PasswordProtected.Length > 0 };
}
public sealed record PortalUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Email { get; set; }
    public required string Name { get; set; }
    public bool Admin { get; set; }
    public bool AllCustomers { get; set; }
    public List<string> CustomerIds { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string Stamp { get; set; } = Security.Token();
    public string PasswordHash { get; set; } = "";
    public string TotpProtected { get; set; } = "";
    public bool MfaConfirmed { get; set; }
    public long LastTotp { get; set; } = -1;
    public List<string> RecoveryHashes { get; set; } = [];
    public string? ActivationHash { get; set; }
    public DateTimeOffset? ActivationExpires { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public object Safe() => new { Id, Email, Name, Admin, AllCustomers, CustomerIds, Enabled, MfaConfirmed, PendingActivation = ActivationHash is not null };
    public bool CanSee(string id) => Admin || AllCustomers || CustomerIds.Contains(id);
}
public sealed record Customer
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public int GraceMinutes { get; set; } = 30;
}
public sealed record AgentRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string CustomerId { get; init; }
    public required string MachineName { get; init; }
    public required string CredentialHash { get; init; }
    public bool Revoked { get; set; }
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? LastGoodCollection { get; set; }
    public DateTimeOffset? SnapshotAt { get; set; }
    public string Version { get; set; } = "";
    public string UpdateStatus { get; set; } = "Awaiting first report";
    public bool UpdateRequested { get; set; }
    public string? VeeamVersion { get; set; }
    public List<string> Issues { get; set; } = [];
    public int PendingReports { get; set; }
    public object Safe() => new { Id, CustomerId, MachineName, Revoked, RegisteredAt, LastSeen, LastGoodCollection, SnapshotAt, Version, UpdateStatus, UpdateRequested, VeeamVersion, Issues, PendingReports };
}
public sealed record StoredJob(string AgentId, string CustomerId, JobInfo Info, DateTimeOffset? ExpectedRun, int? GraceOverride, bool Present = true);
public sealed record JobView(string Key, string AgentId, string ServerName, JobInfo Job, string Result, string Status, bool Overdue, DateTimeOffset? ExpectedRun, int GraceMinutes, JobRun? Latest, DateTimeOffset? LastSuccessfulBackup, bool Stale, bool Present);
public sealed record HeatCell(string JobKey, string Day, string Result, string RunId, string AgentId, int Attempts);
public sealed record AuditEntry(DateTimeOffset Time, string Actor, string Action, string Subject);
