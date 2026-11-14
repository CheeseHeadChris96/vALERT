using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valert.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 32 };
    public static string Version => typeof(Wire).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0-preview.1";
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Empty JSON document.");
}

public sealed record Enrollment(string PortalUrl, string Token, string CustomerName, string UpdatePublicKey, DateTimeOffset ExpiresAt);
public sealed record EnrollmentRequest(string Token, string MachineName, string Version);
public sealed record EnrollmentResponse(string AgentId, string Credential, string CustomerId, int PollMinutes = 15);
public sealed record AgentReport
{
    public string ReportId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
    public string AgentVersion { get; init; } = Wire.Version;
    public string ServerName { get; init; } = Environment.MachineName;
    public string? VeeamVersion { get; init; }
    public string? VeeamServerId { get; init; }
    public bool InventoryComplete { get; init; }
    public bool CollectionSucceeded { get; init; }
    public List<string> Issues { get; init; } = [];
    public List<JobInfo> Jobs { get; init; } = [];
    public List<JobRun> Runs { get; init; } = [];
    public List<RepositoryInfo> Repositories { get; init; } = [];
    public List<OneAlarm> OneAlarms { get; init; } = [];
    public string? OneVersion { get; init; }
    public bool OneConnected { get; init; }
    public JsonElement? OneMetrics { get; init; }
    public string UpdateStatus { get; init; } = "Current";
    public int PendingReports { get; init; }
}
public sealed record JobInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = "Unknown";
    public bool Enabled { get; init; } = true;
    public bool Scheduled { get; init; }
    public string ScheduleFingerprint { get; init; } = "";
    public string ScheduleDescription { get; init; } = "Manual";
    public string? ScheduleIssue { get; init; }
    public DateTimeOffset? NextRun { get; init; }
    public DateTimeOffset? PreviousDue { get; init; }
    public DateTimeOffset? LastStart { get; init; }
    public bool Running { get; init; }
    public int ObjectCount { get; init; }
    public string? RepositoryName { get; init; }
}
public sealed record JobRun
{
    public required string Id { get; init; }
    public required string JobId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string Result { get; init; } = "Unknown";
    public string? Message { get; init; }
    public List<VmResult> Vms { get; init; } = [];
}
public sealed record VmResult(string Name, string Result, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Message);
public sealed record RepositoryInfo(string Id, string Name, string Type, double? CapacityBytes, double? FreeBytes);
public sealed record OneAlarm(string Id, string Name, string Severity, string? ObjectName, DateTimeOffset? Time, string? Message);
public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string Signature);
public sealed record AgentResponse(string ReportId, bool Accepted, UpdateManifest? Update, bool UpdateRequested);

public static class Health
{
    public static string Capacity(double? free, double? capacity) => free is null || capacity is null || capacity <= 0 ? "Unknown" : free / capacity <= .05 ? "Failed" : free / capacity <= .10 ? "Warning" : "Success";
    public static bool IsStale(DateTimeOffset? collected, DateTimeOffset now) => collected is null || now - collected >= TimeSpan.FromMinutes(45);
    public static string Result(string? result) => result?.ToLowerInvariant() switch
    { "success" or "succeeded" => "Success", "warning" => "Warning", "failed" or "failure" => "Failed", _ => "Unknown" };
    public static string Aggregate(IEnumerable<string> statuses)
    {
        var all = statuses.ToList();
        return all.Contains("Failed") ? "Failed" : all.Contains("Warning") ? "Warning" : all.Count == 0 || all.Contains("Unknown") ? "Unknown" : "Success";
    }
    public static TimeZoneInfo Chicago => TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");
    public static string Day(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, Chicago).ToString("yyyy-MM-dd");
    public static bool IsOverdue(JobInfo job, DateTimeOffset? due, int graceMinutes, DateTimeOffset now) =>
        job.Enabled && job.Scheduled && !job.Running && due is not null && now > due.Value.AddMinutes(graceMinutes) && (job.LastStart is null || job.LastStart < due.Value.AddMinutes(-1));
    public static DateTimeOffset? CarryDue(JobInfo? previous, DateTimeOffset? previousDue, JobInfo incoming, DateTimeOffset now)
    {
        if (!incoming.Enabled || !incoming.Scheduled) return null;
        if (previous is not null && previous.ScheduleFingerprint == incoming.ScheduleFingerprint && previousDue is not null &&
            (incoming.LastStart is null || incoming.LastStart < previousDue.Value.AddMinutes(-1))) return previousDue;
        if (incoming.PreviousDue is not null && (incoming.LastStart is null || incoming.LastStart < incoming.PreviousDue.Value.AddMinutes(-1))) return incoming.PreviousDue;
        return incoming.NextRun;
    }
    public static bool Newer(string candidate, string installed)
    {
        static (Version Numeric, int Preview, bool Stable) Parse(string s)
        {
            var split = s.TrimStart('v').Split('-', 2);
            return (System.Version.TryParse(split[0], out var v) ? v : new Version(), split.Length > 1 && int.TryParse(split[1].Split('.').Last(), out var n) ? n : 0, split.Length == 1);
        }
        var a = Parse(candidate); var b = Parse(installed);
        var comparison = a.Numeric.CompareTo(b.Numeric);
        return comparison != 0 ? comparison > 0 : a.Stable != b.Stable ? a.Stable : a.Preview > b.Preview;
    }
}
