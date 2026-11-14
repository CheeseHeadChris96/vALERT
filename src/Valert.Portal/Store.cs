using Microsoft.Data.Sqlite;
using Valert.Core;

namespace Valert.Portal;

public sealed class Store
{
    public object Gate { get; } = new();
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "valert.db");
    public Store(string directory)
    {
        DataDirectory = directory; Directory.CreateDirectory(directory);
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT OR IGNORE INTO meta VALUES('schema','1');
            CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY,email TEXT UNIQUE NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS customers(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS agents(id TEXT PRIMARY KEY,customer_id TEXT NOT NULL,credential_hash TEXT UNIQUE NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS agents_customer ON agents(customer_id);
            CREATE TABLE IF NOT EXISTS enrollments(hash TEXT PRIMARY KEY,customer_id TEXT NOT NULL,expires TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reports(agent_id TEXT NOT NULL,id TEXT NOT NULL,received TEXT NOT NULL,PRIMARY KEY(agent_id,id));
            CREATE TABLE IF NOT EXISTS jobs(agent_id TEXT NOT NULL,id TEXT NOT NULL,customer_id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(agent_id,id));
            CREATE INDEX IF NOT EXISTS jobs_customer ON jobs(customer_id);
            CREATE TABLE IF NOT EXISTS runs(agent_id TEXT NOT NULL,id TEXT NOT NULL,job_id TEXT NOT NULL,started TEXT NOT NULL,ended TEXT,day TEXT NOT NULL,result TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(agent_id,id));
            CREATE INDEX IF NOT EXISTS runs_job_time ON runs(agent_id,job_id,ended DESC);
            CREATE INDEX IF NOT EXISTS runs_retention ON runs(started);
            CREATE INDEX IF NOT EXISTS runs_heat ON runs(agent_id,day,job_id,ended DESC);
            CREATE TABLE IF NOT EXISTS snapshots(agent_id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT,time TEXT NOT NULL,payload TEXT NOT NULL);
            """);
        var schema = Scalar(db, "SELECT value FROM meta WHERE key='schema'")?.ToString();
        if (schema != "1") throw new InvalidOperationException("Database schema is newer than this application. Restore the matching portal version.");
    }
    public SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 30, Pooling = true }.ToString()); db.Open();
        Execute(db, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;"); return db;
    }
    public static SqliteCommand Command(SqliteConnection db, string sql, params object?[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("@p" + i, values[i] ?? DBNull.Value);
        return command;
    }
    public static int Execute(SqliteConnection db, string sql, params object?[] values) { using var c = Command(db, sql, values); return c.ExecuteNonQuery(); }
    public static object? Scalar(SqliteConnection db, string sql, params object?[] values) { using var c = Command(db, sql, values); return c.ExecuteScalar(); }
    public static List<T> Read<T>(SqliteConnection db, string sql, params object?[] values)
    {
        using var c = Command(db, sql, values); using var rows = c.ExecuteReader(); var list = new List<T>();
        while (rows.Read()) list.Add(Wire.Decode<T>(rows.GetString(0))); return list;
    }
    public List<T> Read<T>(string sql, params object?[] values) { lock (Gate) { using var db = Open(); return Read<T>(db, sql, values); } }
    public void Exec(string sql, params object?[] values) { lock (Gate) { using var db = Open(); Execute(db, sql, values); } }
    public List<PortalUser> Users() => Read<PortalUser>("SELECT payload FROM users");
    public PortalUser? User(string id) => Read<PortalUser>("SELECT payload FROM users WHERE id=@p0", id).FirstOrDefault();
    public PortalUser? UserByEmail(string email) => Read<PortalUser>("SELECT payload FROM users WHERE email=@p0", email.Trim().ToLowerInvariant()).FirstOrDefault();
    public void Save(PortalUser user) => Exec("INSERT INTO users VALUES(@p0,@p1,@p2) ON CONFLICT(id) DO UPDATE SET email=excluded.email,payload=excluded.payload", user.Id, user.Email.Trim().ToLowerInvariant(), Wire.Encode(user));
    public List<Customer> Customers() => Read<Customer>("SELECT payload FROM customers");
    public Customer? Customer(string id) => Read<Customer>("SELECT payload FROM customers WHERE id=@p0", id).FirstOrDefault();
    public void Save(Customer customer) => Exec("INSERT INTO customers VALUES(@p0,@p1) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", customer.Id, Wire.Encode(customer));
    public List<AgentRecord> Agents() => Read<AgentRecord>("SELECT payload FROM agents");
    public AgentRecord? Agent(string id) => Read<AgentRecord>("SELECT payload FROM agents WHERE id=@p0", id).FirstOrDefault();
    public AgentRecord? AuthenticateAgent(string credential) => Read<AgentRecord>("SELECT payload FROM agents WHERE credential_hash=@p0", Security.Hash(credential)).FirstOrDefault();
    public void Save(AgentRecord agent) => Exec("INSERT INTO agents VALUES(@p0,@p1,@p2,@p3) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", agent.Id, agent.CustomerId, agent.CredentialHash, Wire.Encode(agent));
    public void Audit(string actor, string action, string subject) => Exec("INSERT INTO audit(time,payload) VALUES(@p0,@p1)", DateTimeOffset.UtcNow.ToString("O"), Wire.Encode(new AuditEntry(DateTimeOffset.UtcNow, actor, action, subject)));
    public List<StoredJob> Jobs(string customerId) => Read<StoredJob>("SELECT payload FROM jobs WHERE customer_id=@p0", customerId);
    public void Save(StoredJob job) => Exec("INSERT INTO jobs VALUES(@p0,@p1,@p2,@p3) ON CONFLICT(agent_id,id) DO UPDATE SET payload=excluded.payload", job.AgentId, job.Info.Id, job.CustomerId, Wire.Encode(job));
    public AgentReport? Snapshot(string agentId) => Read<AgentReport>("SELECT payload FROM snapshots WHERE agent_id=@p0", agentId).FirstOrDefault();
    public JobRun? Run(string agentId, string runId) => Read<JobRun>("SELECT payload FROM runs WHERE agent_id=@p0 AND id=@p1", agentId, runId).FirstOrDefault();
    public List<JobView> JobViews(string customerId, DateTimeOffset now)
    {
        lock (Gate)
        {
            var customer = Customer(customerId)!; var agents = Agents().ToDictionary(a => a.Id); using var db = Open();
            return Jobs(customerId).Select(j =>
            {
                var agent = agents[j.AgentId];
                var latest = Read<JobRun>(db, "SELECT payload FROM runs WHERE agent_id=@p0 AND job_id=@p1 AND ended IS NOT NULL ORDER BY ended DESC LIMIT 1", j.AgentId, j.Info.Id).FirstOrDefault();
                var success = Scalar(db, "SELECT MAX(ended) FROM runs WHERE agent_id=@p0 AND job_id=@p1 AND result='Success'", j.AgentId, j.Info.Id)?.ToString();
                var grace = j.GraceOverride ?? customer.GraceMinutes; var overdue = Health.IsOverdue(j.Info, j.ExpectedRun, grace, now);
                var result = Health.Result(latest?.Result); var stale = agent.Revoked || Health.IsStale(agent.LastGoodCollection, now);
                return new JobView(j.AgentId + ":" + j.Info.Id, j.AgentId, agent.MachineName, j.Info, result, overdue ? "Failed" : result, overdue, j.ExpectedRun, grace, latest is null ? null : latest with { Vms = [] }, DateTimeOffset.TryParse(success, out var lastSuccess) ? lastSuccess : null, stale, j.Present);
            }).ToList();
        }
    }
    public List<HeatCell> Heatmap(string customerId, int days)
    {
        lock (Gate)
        {
            using var db = Open(); using var c = Command(db, """
                SELECT agent_id,job_id,day,result,id,n FROM (
                  SELECT r.*,ROW_NUMBER() OVER(PARTITION BY r.agent_id,r.job_id,r.day ORDER BY r.ended DESC,r.started DESC) AS rn,
                  COUNT(*) OVER(PARTITION BY r.agent_id,r.job_id,r.day) AS n
                  FROM runs r JOIN agents a ON a.id=r.agent_id WHERE a.customer_id=@p0 AND r.ended IS NOT NULL AND r.day>=@p1
                ) WHERE rn=1 ORDER BY day
                """, customerId, Health.Day(DateTimeOffset.UtcNow.AddDays(-(days - 1))));
            using var rows = c.ExecuteReader(); var cells = new List<HeatCell>();
            while (rows.Read()) cells.Add(new(rows.GetString(0) + ":" + rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(0), rows.GetInt32(5))); return cells;
        }
    }
    public void Ingest(AgentRecord authenticated, AgentReport report, DateTimeOffset now)
    {
        if (report.CollectedAt > now.AddMinutes(5) || report.Jobs.Count > 10000 || report.Runs.Count > 100000 || report.Repositories.Count > 10000 || report.ReportId.Length > 100) throw new ArgumentException("Invalid report bounds or collector clock.");
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            if (Scalar(db, "SELECT id FROM reports WHERE agent_id=@p0 AND id=@p1", authenticated.Id, report.ReportId) is not null) return;
            var agent = Agent(authenticated.Id)!;
            if (agent.Revoked || Customer(agent.CustomerId)?.ArchivedAt is not null) throw new UnauthorizedAccessException();
            var newest = agent.SnapshotAt is null || report.CollectedAt >= agent.SnapshotAt;
            foreach (var run in report.Runs)
            {
                if (run.StartedAt < now.AddDays(-90) || run.StartedAt > now.AddMinutes(5) || run.Id.Length > 200 || run.JobId.Length > 200) continue;
                Execute(db, """
                    INSERT INTO runs VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)
                    ON CONFLICT(agent_id,id) DO UPDATE SET ended=excluded.ended,day=excluded.day,result=excluded.result,payload=excluded.payload
                    WHERE (runs.ended IS NULL OR excluded.ended>=runs.ended)
                    """, agent.Id, run.Id, run.JobId, run.StartedAt.ToUniversalTime().ToString("O"), run.EndedAt?.ToUniversalTime().ToString("O"), Health.Day(run.EndedAt ?? run.StartedAt), Health.Result(run.Result), Wire.Encode(run));
            }
            if (newest)
            {
                var oldJobs = Read<StoredJob>(db, "SELECT payload FROM jobs WHERE agent_id=@p0", agent.Id).ToDictionary(j => j.Info.Id);
                foreach (var job in report.Jobs)
                {
                    oldJobs.TryGetValue(job.Id, out var old); var saved = new StoredJob(agent.Id, agent.CustomerId, job, Health.CarryDue(old?.Info, old?.ExpectedRun, job, now), old?.GraceOverride);
                    Execute(db, "INSERT INTO jobs VALUES(@p0,@p1,@p2,@p3) ON CONFLICT(agent_id,id) DO UPDATE SET payload=excluded.payload", agent.Id, job.Id, agent.CustomerId, Wire.Encode(saved));
                }
                if (report.InventoryComplete)
                    foreach (var missing in oldJobs.Values.Where(j => report.Jobs.All(n => n.Id != j.Info.Id)))
                        Execute(db, "UPDATE jobs SET payload=@p0 WHERE agent_id=@p1 AND id=@p2", Wire.Encode(missing with { Present = false }), agent.Id, missing.Info.Id);
                if (report.CollectionSucceeded) agent.LastGoodCollection = report.CollectedAt;
                agent.SnapshotAt = report.CollectedAt; agent.Version = report.AgentVersion; agent.UpdateStatus = report.UpdateStatus; agent.Issues = report.Issues; agent.VeeamVersion = report.VeeamVersion; agent.PendingReports = report.PendingReports;
                Execute(db, "INSERT INTO snapshots VALUES(@p0,@p1) ON CONFLICT(agent_id) DO UPDATE SET payload=excluded.payload", agent.Id, Wire.Encode(report with { Runs = [], Jobs = [] }));
            }
            agent.LastSeen = now;
            Execute(db, "UPDATE agents SET payload=@p0 WHERE id=@p1", Wire.Encode(agent), agent.Id);
            Execute(db, "INSERT INTO reports VALUES(@p0,@p1,@p2)", agent.Id, report.ReportId, now.ToString("O")); tx.Commit();
        }
    }
    public EnrollmentResponse Enroll(EnrollmentRequest request)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); var hash = Security.Hash(request.Token);
            using var query = Command(db, "SELECT customer_id,expires FROM enrollments WHERE hash=@p0", hash); using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new UnauthorizedAccessException("Enrollment token is invalid or already used.");
            var customerId = reader.GetString(0); var expiry = DateTimeOffset.Parse(reader.GetString(1)); reader.Close();
            if (expiry <= DateTimeOffset.UtcNow || Customer(customerId)?.ArchivedAt is not null) throw new UnauthorizedAccessException("Enrollment has expired.");
            var credential = Security.Token(); var agent = new AgentRecord { CustomerId = customerId, MachineName = request.MachineName[..Math.Min(request.MachineName.Length, 200)], CredentialHash = Security.Hash(credential), Version = request.Version };
            Execute(db, "INSERT INTO agents VALUES(@p0,@p1,@p2,@p3)", agent.Id, customerId, agent.CredentialHash, Wire.Encode(agent));
            Execute(db, "DELETE FROM enrollments WHERE hash=@p0", hash); tx.Commit(); Audit("agent", "Agent enrolled", agent.MachineName);
            return new(agent.Id, credential, customerId);
        }
    }
    public void PurgeCustomer(string customerId)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var table in new[] { "runs", "snapshots", "reports" }) Execute(db, $"DELETE FROM {table} WHERE agent_id IN (SELECT id FROM agents WHERE customer_id=@p0)", customerId);
            Execute(db, "DELETE FROM jobs WHERE customer_id=@p0", customerId); Execute(db, "DELETE FROM enrollments WHERE customer_id=@p0", customerId); Execute(db, "DELETE FROM agents WHERE customer_id=@p0", customerId); Execute(db, "DELETE FROM customers WHERE id=@p0", customerId); tx.Commit();
        }
    }
    public void Retain(DateTimeOffset now)
    {
        Exec("DELETE FROM runs WHERE started<@p0; DELETE FROM reports WHERE received<@p0; DELETE FROM audit WHERE time<@p0; DELETE FROM enrollments WHERE expires<@p1", now.AddDays(-90).ToString("O"), now.ToString("O"));
        foreach (var c in Customers().Where(c => c.ArchivedAt < now.AddDays(-90))) PurgeCustomer(c.Id);
    }
}
