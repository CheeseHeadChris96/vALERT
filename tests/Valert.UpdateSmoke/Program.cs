using System.IO.Compression;
using Valert.Core;
using Valert.Updater;
using Microsoft.Data.Sqlite;

if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
var install=Wire.Decode<ConsoleInstall>(File.ReadAllText(Path.Combine(args[0],"tools","console-install.json")));
var service=new WindowsConsoleService(install);var engine=new ConsoleUpdateEngine(install,new HeldStartup(service));
await service.Stop();
var marker=Path.Combine(install.DataDirectory,"update-smoke-data.txt");File.WriteAllText(marker,"synthetic data preserved across console upgrades");
var beforeDb=Security.HashFile(Path.Combine(install.DataDirectory,"valert.db"));
using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(install.DataDirectory,"valert.db"),Pooling=false}.ToString())){db.Open();using var command=db.CreateCommand();command.CommandText="INSERT INTO meta(key,value) VALUES('upgrade-smoke-identity','retained-identity')";command.ExecuteNonQuery();}
var settings=Path.Combine(install.DataDirectory,"portal-settings.json");
File.WriteAllText(settings,"{\"automaticUpdateChecks\":false,\"publicUrl\":\"https://localhost\"}");
await service.Start();
if(!await service.Healthy(args[2]))throw new Exception("Original console is not healthy.");
await engine.Install(args[1],args[2]);
if(!await service.Healthy(args[2]))throw new Exception("Updated console is not healthy.");
if(File.ReadAllText(marker)!="synthetic data preserved across console upgrades")throw new Exception("Console update changed retained data.");
if(File.Exists(engine.JournalPath))throw new Exception("Update journal was not committed.");
var goodCommand=service.Command;
// A valid archive with an invalid executable exercises real Windows rollback and data restoration.
var broken=Path.Combine(engine.Operations,"broken-smoke.zip");
using(var zip=ZipFile.Open(broken,ZipArchiveMode.Create))
foreach(var name in new[]{"Valert.Portal.exe","wwwroot/index.html"}){using var w=new StreamWriter(zip.CreateEntry(name).Open());w.Write("invalid executable for rollback test");}
try{await engine.Install(broken,"999.0.0-preview.1");throw new Exception("Invalid executable was accepted.");}catch(InvalidOperationException){}
if(service.Command!=goodCommand||!await service.Healthy(args[2]))throw new Exception("Windows console did not recover after failed startup.");
if(File.ReadAllText(marker)!="synthetic data preserved across console upgrades"||!File.Exists(settings))throw new Exception("Rollback lost retained data or configuration.");
if(File.Exists(engine.JournalPath))throw new Exception("Rollback journal was not completed.");
await service.Stop();
using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(install.DataDirectory,"valert.db"),Pooling=false}.ToString())){db.Open();using var command=db.CreateCommand();command.CommandText="SELECT value FROM meta WHERE key='upgrade-smoke-identity'";if(command.ExecuteScalar()?.ToString()!="retained-identity")throw new Exception("Update or rollback lost database identity data.");}
// Hash the database only after the service closes all SQLite connections; opening it may checkpoint its WAL.
Console.WriteLine("Windows console update, startup validation, real failed-executable rollback, and retained data checks passed. Initial database SHA-256: "+beforeDb);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class HeldStartup(WindowsConsoleService inner):IConsoleService
{
    public string Command=>inner.Command;public Task Stop()=>inner.Stop();public Task SetCommand(string c)=>inner.SetCommand(c);public Task Start()=>inner.Start();public Task ProtectDirectory(string d)=>inner.ProtectDirectory(d);public Task RestoreDataAccess(string d)=>inner.RestoreDataAccess(d);
    public async Task<bool> Healthy(string version)
    {
        if(!await inner.Healthy(version))return false;
        using var http=new HttpClient();
        using var agentResponse=await http.PostAsync("http://127.0.0.1:5080/api/agents/reports",new StringContent("{}",System.Text.Encoding.UTF8,"application/json"));
        if(agentResponse.StatusCode!=System.Net.HttpStatusCode.BadRequest)throw new Exception("Candidate console did not reject plaintext agent traffic.");
        using var response=await http.PostAsync("http://127.0.0.1:5080/api/auth/setup",new StringContent("{}",System.Text.Encoding.UTF8,"application/json"));
        if(response.StatusCode!=System.Net.HttpStatusCode.ServiceUnavailable)throw new Exception("Candidate console did not hold setup mutations before update commit.");return true;
    }
}
