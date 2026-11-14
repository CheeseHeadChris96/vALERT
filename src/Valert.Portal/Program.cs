using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Valert.Core;
using Valert.Portal;

var dataDirectory = Environment.GetEnvironmentVariable("VALERT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "vALERT", "Portal");
if (args.Contains("--restore"))
{
    var index = Array.IndexOf(args, "--restore");
    if (index + 1 >= args.Length) throw new ArgumentException("Usage: Valert.Portal --restore archive.vab; supply passphrase on stdin.");
    Platform.Restore(dataDirectory, args[index + 1], Console.ReadLine() ?? ""); Console.WriteLine("Migration restored. Configure the HTTPS certificate on this server, then start the portal service."); return;
}
var store = new Store(dataDirectory); var platform = new Platform(store);
if (args.Contains("--recover-admin"))
{
    var index = Array.IndexOf(args, "--recover-admin"); var user = store.UserByEmail(args[index + 1]) ?? throw new ArgumentException("Administrator not found.");
    if (!user.Admin) throw new ArgumentException("Account is not an administrator.");
    var token = Security.Token(); user.ActivationHash = Security.Hash(token); user.ActivationExpires = DateTimeOffset.UtcNow.AddHours(1); user.TotpProtected = ""; user.MfaConfirmed = false; user.Enabled = true; user.Stamp = Security.Token(); store.Save(user);
    Security.AtomicWrite(Path.Combine(dataDirectory, "admin-recovery.txt"), platform.Settings.PublicUrl + "/#activate=" + token); store.Audit("local administrator", "Admin recovery prepared", user.Email); Console.WriteLine("Recovery link written to the protected data directory: admin-recovery.txt"); return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args=args, ContentRootPath=Directory.Exists(Path.Combine(AppContext.BaseDirectory,"wwwroot"))?AppContext.BaseDirectory:null });
builder.Services.AddWindowsService(options => options.ServiceName = "vALERT.Portal");
builder.Services.AddSingleton(store); builder.Services.AddSingleton(platform);
builder.Services.AddSingleton<ConsoleUpdates>();
builder.Services.AddSingleton<IMailTransport,SmtpTransport>();builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<Releases>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<Releases>());
builder.Services.AddDataProtection().SetApplicationName("vALERT").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "valert.csrf"; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = Wire.Json.PropertyNamingPolicy; });
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "valert.session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async c =>
    {
        var user = store.User(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        if (user is null || !user.Enabled || !user.MfaConfirmed || user.Stamp != c.Principal?.FindFirstValue("stamp")) { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("Admin")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
        options.Listen(IPAddress.Loopback, platform.Settings.LocalPort);
        if (!string.IsNullOrWhiteSpace(platform.Settings.CertificateThumbprint))
        {
            using var certificates = new X509Store(StoreName.My, StoreLocation.LocalMachine); certificates.Open(OpenFlags.ReadOnly);
            var certificate = certificates.Certificates.Find(X509FindType.FindByThumbprint, platform.Settings.CertificateThumbprint, true).OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey) ?? throw new InvalidOperationException("Configured HTTPS certificate was not found or has no accessible private key.");
            options.ListenAnyIP(platform.Settings.HttpsPort, listen => listen.UseHttps(certificate));
        }
    });
}
var app = builder.Build();
if (store.Users().Count == 0 && !File.Exists(Path.Combine(dataDirectory, "initial-setup.txt")))
    Security.AtomicWrite(Path.Combine(dataDirectory, "initial-setup.txt"), Security.Token());
_ = platform.PublicKey;
var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("vALERT.AccountSecrets.v1");
var pendingProtector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("vALERT.PendingLogin.v1");
var writeGate = new SemaphoreSlim(1,1);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.IsHttps) context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    if (!context.Request.IsHttps && context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address)) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "HTTPS is required." }); return; }
    try { await next(); }
    catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException or FormatException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (UnauthorizedAccessException) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Access denied." }); }
    catch (Exception e) { app.Logger.LogError(e, "Request failed for {Path}", context.Request.Path); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed. Check the portal logs." }); }
});
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var mutation = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
    if(mutation&&ConsoleUpdates.Held){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Console update startup validation is in progress. Retry shortly."});return;}
    if (mutation)
    {
        if (!context.Request.Path.StartsWithSegments("/api/agents") && !context.Request.Path.StartsWithSegments("/api/enrollment"))
        {
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Session protection expired. Refresh this page." }); return; }
        }
    }
    if (!mutation) { await next(); return; }
    await writeGate.WaitAsync(context.RequestAborted);
    try
    {
        if (platform.Settings.MigrationPaused && context.Request.Path.StartsWithSegments("/api/admin") && context.Request.Path != "/api/admin/migration/resume")
        { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "This portal is frozen for migration. Resume it only if the destination has not taken over." }); return; }
        await next();
    }
    finally { writeGate.Release(); }
});
app.UseDefaultFiles(); app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

PortalUser Current(HttpContext c) => store.User(c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "") ?? throw new UnauthorizedAccessException();
void Access(HttpContext c, string customerId) { if (!Current(c).CanSee(customerId) || store.Customer(customerId) is null) throw new UnauthorizedAccessException(); }
void PasswordCheck(string password) { if (password.Length < 12 || password.Length > 128) throw new ArgumentException("Use a password between 12 and 128 characters."); }
void Fail(PortalUser user) { user.FailedAttempts++; if (user.FailedAttempts >= 5) { user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15); user.FailedAttempts = 0; } store.Save(user); }
bool Mfa(PortalUser user, string code)
{
    var secret = protector.Unprotect(user.TotpProtected); var step = Security.VerifyTotp(secret, code.Replace(" ", ""), DateTimeOffset.UtcNow, user.LastTotp);
    if (step is not null) { user.LastTotp = step.Value; return true; }
    var hash = Security.Hash(code.Trim()); if (user.MfaConfirmed && user.RecoveryHashes.Remove(hash)) return true; return false;
}
void StepUp(HttpContext context, string code)
{
    lock (store.Gate) { var user = Current(context); if (user.LockedUntil > DateTimeOffset.UtcNow || !Mfa(user, code)) { Fail(user); throw new ArgumentException("Invalid or already-used MFA code. Use the next authenticator code."); } store.Save(user); }
}
object Pending(HttpContext context, PortalUser user)
{
    if (string.IsNullOrEmpty(user.TotpProtected)) { user.TotpProtected = protector.Protect(Security.TotpSecret()); store.Save(user); }
    var ticket = Wire.Encode(new PendingLogin(user.Id, user.Stamp, DateTimeOffset.UtcNow.AddMinutes(10)));
    context.Response.Cookies.Append("valert.pending", pendingProtector.Protect(ticket), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(10), Path = "/api/auth" });
    var secret = user.MfaConfirmed ? null : protector.Unprotect(user.TotpProtected);
    return new { stage = user.MfaConfirmed ? "mfa" : "enroll", secret, uri = secret is null ? null : $"otpauth://totp/vALERT:{Uri.EscapeDataString(user.Email)}?secret={secret}&issuer=vALERT&digits=6&period=30", email = user.Email };
}
app.MapGet("/health", () => Results.Ok(new { status = "ready", version = Wire.Version, migrationPaused = platform.Settings.MigrationPaused }));
var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");
auth.MapGet("/session", (HttpContext c, IAntiforgery anti) => new { user = c.User.Identity?.IsAuthenticated == true ? Current(c).Safe() : null, csrf = anti.GetAndStoreTokens(c).RequestToken, setupRequired = store.Users().Count == 0, version = Wire.Version });
auth.MapPost("/setup", (HttpContext c, SetupRequest input) =>
{
    lock (store.Gate)
    {
        var path = Path.Combine(dataDirectory,"initial-setup.txt");
        if (store.Users().Count != 0 || !File.Exists(path) || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Security.Hash(input.Token)), System.Text.Encoding.UTF8.GetBytes(Security.Hash(File.ReadAllText(path).Trim())))) throw new UnauthorizedAccessException();
        PasswordCheck(input.Password); ValidateEmail(input.Email);
        var user = new PortalUser { Email = input.Email.Trim(), Name = input.Name.Trim(), Admin = true, AllCustomers = true, PasswordHash = Security.Password(input.Password) }; store.Save(user); File.Delete(path); store.Audit(user.Email, "Initial administrator created", user.Id); return Pending(c,user);
    }
});
auth.MapPost("/login", (HttpContext c, LoginRequest input) =>
{
    lock (store.Gate)
    {
        var user = store.UserByEmail(input.Email);
        if (user is null || !user.Enabled || user.LockedUntil > DateTimeOffset.UtcNow || user.ActivationHash is not null || !Security.VerifyPassword(input.Password, user.PasswordHash))
        { if (user is not null) Fail(user); return Results.Json(new { error = "Login failed. Check your credentials or try again later." }, statusCode: 401); }
        return Results.Ok(Pending(c,user));
    }
});
auth.MapPost("/activate", (HttpContext c, ActivationRequest input) =>
{
    lock (store.Gate)
    {
        var user = store.Users().FirstOrDefault(u => u.ActivationHash == Security.Hash(input.Token) && u.ActivationExpires > DateTimeOffset.UtcNow && u.Enabled) ?? throw new UnauthorizedAccessException();
        PasswordCheck(input.Password); user.PasswordHash = Security.Password(input.Password); user.ActivationHash = null; user.ActivationExpires = null; user.Stamp = Security.Token(); store.Save(user); return Pending(c,user);
    }
});
auth.MapPost("/mfa", async (HttpContext c, CodeRequest input) =>
{
    PortalUser user; List<string>? recovery = null;
    lock (store.Gate)
    {
        PendingLogin ticket;
        try { ticket = Wire.Decode<PendingLogin>(pendingProtector.Unprotect(c.Request.Cookies["valert.pending"] ?? "")); } catch (CryptographicException) { return Results.Unauthorized(); }
        user = store.User(ticket.Id) ?? throw new UnauthorizedAccessException();
        if (ticket.Expires < DateTimeOffset.UtcNow || ticket.Stamp != user.Stamp || !user.Enabled || user.LockedUntil > DateTimeOffset.UtcNow) return Results.Unauthorized();
        if (!Mfa(user,input.Code)) { Fail(user); return Results.Json(new { error = "Invalid or already-used MFA code." }, statusCode: 401); }
        if (!user.MfaConfirmed) { recovery = Enumerable.Range(0,10).Select(_ => Security.Token(10)).ToList(); user.RecoveryHashes = recovery.Select(Security.Hash).ToList(); }
        user.MfaConfirmed = true; user.FailedAttempts = 0; user.LockedUntil = null; store.Save(user); store.Audit(user.Email, "Signed in with MFA", user.Id);
    }
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier,user.Id), new Claim(ClaimTypes.Name,user.Email), new Claim(ClaimTypes.Role,user.Admin?"Admin":"Viewer"), new Claim("stamp",user.Stamp) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await c.SignInAsync(new ClaimsPrincipal(identity)); c.Response.Cookies.Delete("valert.pending", new CookieOptions { Path = "/api/auth" }); return Results.Ok(new { recoveryCodes = recovery });
});
auth.MapPost("/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(); });

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/overview", (HttpContext c) =>
{
    var user = Current(c); var now = DateTimeOffset.UtcNow; var agents = store.Agents();
    var customers = store.Customers().Where(x => user.CanSee(x.Id) && x.ArchivedAt is null).Select(customer =>
    {
        var ownAgents = agents.Where(a => a.CustomerId == customer.Id && !a.Revoked).ToList(); var jobs = store.JobViews(customer.Id, now).Where(j => j.Present && j.Job.Enabled).ToList();
        var stale = ownAgents.Count == 0 || ownAgents.Any(a => Health.IsStale(a.LastGoodCollection,now));
        var counts = jobs.GroupBy(j => j.Stale ? "Unknown" : j.Status).ToDictionary(g => g.Key,g => g.Count());
        return new { customer.Id, customer.Name, customer.Notes, status = Health.Aggregate(jobs.Select(j => j.Stale ? "Unknown" : j.Status)), stale, overdue = jobs.Count(j => j.Overdue), counts, jobs = jobs.Count, servers = ownAgents.Count, lastReport = ownAgents.Max(a => a.LastGoodCollection), capacity = Health.Aggregate(ownAgents.SelectMany(a => store.Snapshot(a.Id)?.Repositories ?? []).Select(r => Health.Capacity(r.FreeBytes,r.CapacityBytes))) };
    }).ToList();
    return new { customers, generatedAt = now, timezone = "America/Chicago", version = Wire.Version };
});
api.MapGet("/customers/{id}", (HttpContext c, string id, int? days) =>
{
    Access(c,id); var agents = store.Agents().Where(a => a.CustomerId == id).ToList(); var jobs = store.JobViews(id,DateTimeOffset.UtcNow);
    return new { customer = store.Customer(id), agents = agents.Select(a => a.Safe()), jobs, heatmap = store.Heatmap(id,Math.Clamp(days ?? 30,1,90)), snapshots = agents.Select(a => new { agentId = a.Id, serverName = a.MachineName, report = store.Snapshot(a.Id) }), release = app.Services.GetRequiredService<Releases>().Current?.Version };
});
api.MapGet("/customers/{id}/runs/{agentId}/{runId}", (HttpContext c, string id, string agentId, string runId) =>
{
    Access(c,id); if (store.Agent(agentId)?.CustomerId != id) throw new UnauthorizedAccessException();
    return Results.Ok(store.Run(agentId,runId) ?? throw new ArgumentException("Run is no longer retained."));
});
api.MapGet("/customers/{id}/jobs/{agentId}/{jobId}/history", (HttpContext c, string id, string agentId, string jobId, int? offset) =>
{
    Access(c,id); if (store.Agent(agentId)?.CustomerId != id) throw new UnauthorizedAccessException();
    return store.Read<JobRun>("SELECT payload FROM runs WHERE agent_id=@p0 AND job_id=@p1 ORDER BY started DESC LIMIT 100 OFFSET @p2", agentId, jobId, Math.Max(0,offset ?? 0));
});
api.MapGet("/customers/{id}/jobs/{agentId}/{jobId}/vm-success", (HttpContext c, string id, string agentId, string jobId) =>
{
    Access(c,id); if (store.Agent(agentId)?.CustomerId != id) throw new UnauthorizedAccessException();
    return store.VmLastSuccess(agentId,jobId);
});
var admin = app.MapGroup("/api/admin").RequireAuthorization("admin");
admin.MapGet("/state", (Releases releases,ConsoleUpdates updates) => new { customers = store.Customers(), users = store.Users().Select(u => u.Safe()), agents = store.Agents().Select(a => a.Safe()), settings = platform.Settings.Safe(), console = releases.ConsoleState(updates), release = releases.Current?.Version, releaseStatus = releases.Status, audit = store.Read<AuditEntry>("SELECT payload FROM audit ORDER BY id DESC LIMIT 100") });
admin.MapPost("/customers", (HttpContext c, CustomerRequest input) => { ValidateName(input.Name); var customer = new Customer { Name = input.Name.Trim(), Notes = input.Notes ?? "" }; store.Save(customer); store.Audit(Current(c).Email,"Customer created",customer.Name); return customer; });
admin.MapPut("/customers/{id}", (HttpContext c, string id, CustomerRequest input) => { var customer = store.Customer(id) ?? throw new ArgumentException("Unknown customer."); ValidateName(input.Name); customer.Name = input.Name.Trim(); customer.Notes = input.Notes ?? ""; customer.GraceMinutes = Math.Clamp(input.GraceMinutes,0,10080); store.Save(customer); store.Audit(Current(c).Email,"Customer updated",customer.Name); return customer; });
admin.MapPost("/customers/{id}/archive", (HttpContext c, string id) =>
{
    lock (store.Gate) { var customer = store.Customer(id) ?? throw new ArgumentException("Unknown customer."); customer.ArchivedAt = DateTimeOffset.UtcNow; store.Save(customer); foreach (var a in store.Agents().Where(a => a.CustomerId == id)) { a.Revoked = true; store.Save(a); } store.Exec("DELETE FROM enrollments WHERE customer_id=@p0",id); store.Audit(Current(c).Email,"Customer archived and agents revoked",customer.Name); } return Results.Ok();
});
admin.MapPost("/customers/{id}/restore", (HttpContext c,string id) => { var customer = store.Customer(id) ?? throw new ArgumentException("Unknown customer."); customer.ArchivedAt = null; store.Save(customer); store.Audit(Current(c).Email,"Customer unarchived; re-enrollment required",customer.Name); return Results.Ok(); });
admin.MapDelete("/customers/{id}", (HttpContext c, string id, [Microsoft.AspNetCore.Mvc.FromBody] CodeRequest input) => { StepUp(c,input.Code); var customer = store.Customer(id) ?? throw new ArgumentException("Unknown customer."); store.PurgeCustomer(id); store.Audit(Current(c).Email,"Customer permanently deleted",customer.Name); return Results.Ok(); });
admin.MapPost("/customers/{id}/enrollment", (HttpContext c,string id) => { var customer = store.Customer(id) ?? throw new ArgumentException("Unknown customer."); if (customer.ArchivedAt is not null) throw new ArgumentException("Unarchive this customer before enrollment."); var bytes = platform.EnrollmentPackage(customer); store.Audit(Current(c).Email,"Enrollment package generated",customer.Name); return Results.File(bytes,"application/zip","vALERT-Enrollment-" + id[..8] + ".zip"); });
admin.MapPost("/jobs/{agentId}/{jobId}/grace", (HttpContext c,string agentId,string jobId,GraceRequest input) => { var a = store.Agent(agentId) ?? throw new ArgumentException("Unknown agent."); var job = store.Jobs(a.CustomerId).First(j => j.AgentId == agentId && j.Info.Id == jobId); store.Save(job with { GraceOverride = input.Minutes is null ? null : Math.Clamp(input.Minutes.Value,0,10080) }); store.Audit(Current(c).Email,"Job grace period changed",job.Info.Name); return Results.Ok(); });
admin.MapPost("/users", (HttpContext c, UserRequest input) =>
{
    ValidateEmail(input.Email); ValidateName(input.Name); if (store.UserByEmail(input.Email) is not null) throw new ArgumentException("Email is already in use.");
    var token = Security.Token(); var user = new PortalUser { Email = input.Email.Trim(), Name = input.Name.Trim(), Admin = input.Admin, AllCustomers = input.AllCustomers, CustomerIds = input.CustomerIds ?? [], ActivationHash = Security.Hash(token), ActivationExpires = DateTimeOffset.UtcNow.AddHours(24) }; store.Save(user); store.Audit(Current(c).Email,"User invited",user.Email); return new { user = user.Safe(), activationUrl = platform.Settings.PublicUrl.TrimEnd('/') + "/#activate=" + token };
});
admin.MapPut("/users/{id}", (HttpContext c,string id,UserRequest input) =>
{
    lock (store.Gate) { var user = store.User(id) ?? throw new ArgumentException("Unknown user."); if (user.Id == Current(c).Id && (!input.Enabled || !input.Admin)) throw new ArgumentException("You cannot remove your own administrator access."); ValidateName(input.Name); user.Name = input.Name.Trim(); user.Admin = input.Admin; user.AllCustomers = input.AllCustomers; user.CustomerIds = input.CustomerIds ?? []; user.Enabled = input.Enabled; user.Stamp = Security.Token(); store.Save(user); store.Audit(Current(c).Email,"User access changed; sessions revoked",user.Email); return user.Safe(); }
});
admin.MapPost("/users/{id}/reset", (HttpContext c,string id,CodeRequest input) =>
{
    StepUp(c,input.Code); var user = store.User(id) ?? throw new ArgumentException("Unknown user."); var token = Security.Token(); user.ActivationHash = Security.Hash(token); user.ActivationExpires = DateTimeOffset.UtcNow.AddHours(1); user.MfaConfirmed = false; user.TotpProtected = ""; user.LastTotp = -1; user.RecoveryHashes.Clear(); user.Stamp = Security.Token(); user.LockedUntil = null; user.FailedAttempts = 0; store.Save(user); store.Audit(Current(c).Email,"Account recovery issued",user.Email); return new { activationUrl = platform.Settings.PublicUrl.TrimEnd('/') + "/#activate=" + token };
});
admin.MapPost("/agents/{id}/update", (HttpContext c,string id) => { var agent = store.Agent(id) ?? throw new ArgumentException("Unknown agent."); agent.UpdateRequested = true; store.Save(agent); store.Audit(Current(c).Email,"Agent update requested",agent.MachineName); return Results.Ok(); });
admin.MapPost("/agents/{id}/revoke", (HttpContext c,string id) => { var agent = store.Agent(id) ?? throw new ArgumentException("Unknown agent."); agent.Revoked = true; store.Save(agent); store.Audit(Current(c).Email,"Agent revoked",agent.MachineName); return Results.Ok(); });
admin.MapPut("/settings", (HttpContext c, SettingsRequest input) => { if (!Uri.TryCreate(input.PublicUrl,UriKind.Absolute,out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/") throw new ArgumentException("Enter an HTTPS origin, such as https://valert.example.com."); platform.Settings.PublicUrl = uri.GetLeftPart(UriPartial.Authority); platform.Settings.IncludePreviewUpdates = input.IncludePreviewUpdates; platform.Settings.AutomaticUpdateChecks = input.AutomaticUpdateChecks; platform.SaveSettings(); store.Audit(Current(c).Email,"Portal settings updated",platform.Settings.PublicUrl); return Results.Ok(); });
admin.MapPost("/releases/refresh", async (Releases releases, CancellationToken token) => { await releases.Refresh(token); return Results.Ok(new { releases.Status }); });
admin.MapPost("/console/download",async (HttpContext c,Releases releases,CancellationToken token)=>{await releases.DownloadConsole(token);store.Audit(Current(c).Email,"Console installer downloaded and verified",releases.Published?.Version??"");return Results.Ok();});
admin.MapGet("/console/installer",(Releases releases)=>releases.ConsoleInstallerPath is { } path&&File.Exists(path)?Results.File(path,"application/octet-stream","vALERT-Console-Setup.exe",enableRangeProcessing:true):Results.NotFound());
admin.MapPost("/console/install",(HttpContext c,ConsoleInstallRequest input,ConsoleUpdates updates,Releases releases)=>{
    StepUp(c,input.Code);if(releases.Published?.Version!=input.Version)throw new ArgumentException("Release changed. Check for updates and select the current release.");
    updates.Queue(input.Version);store.Audit(Current(c).Email,"Console update requested",input.Version);return Results.Accepted(value:new{message="Update queued. The console will restart; agents retain reports during the interruption."});
});
admin.MapPut("/smtp",(HttpContext c,SmtpRequest input,MailService mail)=>{mail.Configure(input);store.Audit(Current(c).Email,"SMTP configuration updated",input.Enabled?"Enabled":"Disabled");return Results.Ok();});
admin.MapPost("/smtp/test",async(HttpContext c,SmtpTestRequest input,MailService mail,CancellationToken token)=>{
    try{await mail.SendTest(input.Recipient,token);}catch(ArgumentException){throw;}catch(Exception e)when(e is not OperationCanceledException){store.Audit(Current(c).Email,"SMTP test failed",e.GetType().Name);return Results.BadRequest(new{error="SMTP delivery failed. Check the host, port, TLS mode, and credentials."});}
    store.Audit(Current(c).Email,"SMTP test sent",input.Recipient);return Results.Ok(new{message="Test email accepted by the SMTP server."});
});
admin.MapPost("/migration/export", (HttpContext c,MigrationRequest input) => { StepUp(c,input.Code); if (input.Password.Length < 16) throw new ArgumentException("Use a migration passphrase of at least 16 characters."); store.Audit(Current(c).Email,"Migration freeze and export",Wire.Version); var data = platform.Export(input.Password); return Results.File(data,"application/octet-stream","vALERT-Migration-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".vab"); });
admin.MapPost("/migration/resume", (HttpContext c) => { lock(store.Gate) { platform.Settings.MigrationPaused = false; platform.SaveSettings(); store.Audit(Current(c).Email,"Old portal resumed; do not run both copies",Wire.Version); } return Results.Ok(); });

var agentApi = app.MapGroup("/api/agents");
AgentRecord AgentAuth(HttpContext c)
{
    var authorization = c.Request.Headers.Authorization.ToString(); if (!authorization.StartsWith("Bearer ",StringComparison.Ordinal)) throw new UnauthorizedAccessException();
    var agent = store.AuthenticateAgent(authorization[7..]); if (agent is null || agent.Revoked || store.Customer(agent.CustomerId)?.ArchivedAt is not null) throw new UnauthorizedAccessException(); return agent;
}
agentApi.MapPost("/enroll", (EnrollmentRequest input) => { if (platform.Settings.MigrationPaused) return Results.StatusCode(503); return Results.Ok(store.Enroll(input)); }).RequireRateLimiting("auth");
agentApi.MapPost("/reports", (HttpContext c,AgentReport report,Releases releases) =>
{
    lock (store.Gate)
    {
        if (platform.Settings.MigrationPaused) return Results.StatusCode(503);
        var agent = AgentAuth(c); store.Ingest(agent,report,DateTimeOffset.UtcNow);
        var update = releases.Current is { } manifest && Health.Newer(manifest.Version,report.AgentVersion) ? manifest : null;
        var requested = agent.UpdateRequested;
        if (requested) { agent = store.Agent(agent.Id)!; agent.UpdateRequested = false; store.Save(agent); }
        return Results.Ok(new AgentResponse(report.ReportId,true,update,requested));
    }
});
agentApi.MapGet("/package/{version}", (HttpContext c,string version,Releases releases) => { AgentAuth(c); if (releases.Current?.Version != version || releases.PackagePath is null) return Results.NotFound(); return Results.File(releases.PackagePath,"application/zip",enableRangeProcessing:true); });
app.MapPost("/api/enrollment/installer", (TokenRequest input, Releases releases) =>
{
    using var db = store.Open(); var expiry = Store.Scalar(db,"SELECT expires FROM enrollments WHERE hash=@p0",Security.Hash(input.Token))?.ToString();
    if (!DateTimeOffset.TryParse(expiry,out var expires) || expires < DateTimeOffset.UtcNow) throw new UnauthorizedAccessException();
    if (releases.Current is null || releases.InstallerHash is null) throw new ArgumentException("No installer release is cached. Ask the administrator to refresh releases.");
    return new { url = "https://github.com/CheeseHeadChris96/vALERT/releases/download/v" + releases.Current.Version + "/vALERT-Agent-Setup.exe", sha256 = releases.InstallerHash };
}).RequireRateLimiting("auth");
app.MapFallbackToFile("index.html");
app.Run();

static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 150) throw new ArgumentException("Name must contain 1 to 150 characters."); }
static void ValidateEmail(string email) { if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email,out _)) throw new ArgumentException("Enter a valid email address."); }
public partial class Program { }
record PendingLogin(string Id,string Stamp,DateTimeOffset Expires);
record SetupRequest(string Token,string Name,string Email,string Password);
record LoginRequest(string Email,string Password);
record ActivationRequest(string Token,string Password);
record CodeRequest(string Code);
record TokenRequest(string Token);
record CustomerRequest(string Name,string? Notes,int GraceMinutes = 30);
record GraceRequest(int? Minutes);
record UserRequest(string Name,string Email,bool Admin,bool AllCustomers,List<string>? CustomerIds,bool Enabled = true);
record SettingsRequest(string PublicUrl,bool IncludePreviewUpdates,bool AutomaticUpdateChecks=true);
record ConsoleInstallRequest(string Version,string Code);
record SmtpTestRequest(string Recipient);
record MigrationRequest(string Password,string Code);
