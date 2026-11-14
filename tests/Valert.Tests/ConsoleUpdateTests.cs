using System.IO.Compression;
using Valert.Core;
using Xunit;

namespace Valert.Tests;

public sealed class ConsoleUpdateTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"valert-update-test-"+Guid.NewGuid());
    readonly ConsoleInstall install;
    readonly FakeConsoleService host=new();
    public ConsoleUpdateTests(){install=new(Path.Combine(root,"app"),Path.Combine(root,"data"));Directory.CreateDirectory(install.InstallRoot);Directory.CreateDirectory(install.DataDirectory);File.WriteAllText(Path.Combine(install.DataDirectory,"valert.db"),"original history");Directory.CreateDirectory(Path.Combine(install.DataDirectory,"keys"));File.WriteAllText(Path.Combine(install.DataDirectory,"keys","key.xml"),"original MFA key");}
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    string Package()
    {
        var path=Path.Combine(root,"package.zip");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        foreach(var name in new[]{"Valert.Portal.exe","wwwroot/index.html"}){using var w=new StreamWriter(zip.CreateEntry(name).Open());w.Write("synthetic executable");}return path;
    }
    [Fact] public async Task SuccessfulUpdatePreservesDataAndReleasesWriteHold()
    {
        var engine=new ConsoleUpdateEngine(install,host);await engine.Install(Package(),"0.2.0");
        Assert.Equal("original history",File.ReadAllText(Path.Combine(install.DataDirectory,"valert.db")));Assert.Equal("original MFA key",File.ReadAllText(Path.Combine(install.DataDirectory,"keys","key.xml")));
        Assert.False(File.Exists(engine.JournalPath));Assert.Empty(Directory.GetFiles(install.InstallRoot,"update-hold",SearchOption.AllDirectories));Assert.Contains("portal-versions",host.Command);
    }
    [Fact] public async Task FailedStartupRestoresPreviousBinaryAndFullDataSnapshot()
    {
        host.Ready=false;host.OnHealth=()=>File.WriteAllText(Path.Combine(install.DataDirectory,"valert.db"),"candidate migration");var engine=new ConsoleUpdateEngine(install,host);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.Install(Package(),"0.2.0"));Assert.Equal("old-console",host.Command);
        Assert.Equal("original history",File.ReadAllText(Path.Combine(install.DataDirectory,"valert.db")));Assert.Equal("original MFA key",File.ReadAllText(Path.Combine(install.DataDirectory,"keys","key.xml")));Assert.False(File.Exists(engine.JournalPath));
    }
    [Fact] public async Task CommittedRecoveryNeverOverwritesAcceptedReports()
    {
        var engine=new ConsoleUpdateEngine(install,host);Directory.CreateDirectory(engine.Operations);var target=Path.Combine(install.InstallRoot,"new");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"update-hold"),"hold");
        Security.AtomicWrite(engine.JournalPath,Wire.Encode(new ConsoleUpdateJournal("old-console",target,"missing-backup","0.2.0",true)));
        File.WriteAllText(Path.Combine(install.DataDirectory,"valert.db"),"new accepted reports");await engine.Recover();Assert.Equal("new accepted reports",File.ReadAllText(Path.Combine(install.DataDirectory,"valert.db")));Assert.False(File.Exists(Path.Combine(target,"update-hold")));
    }
    [Fact] public async Task InterruptedUncommittedUpdateRestoresTheSnapshot()
    {
        var engine=new ConsoleUpdateEngine(install,host);var backup=Path.Combine(engine.Operations,"backup");Directory.CreateDirectory(backup);File.WriteAllText(Path.Combine(backup,"valert.db"),"saved history");
        Security.AtomicWrite(engine.JournalPath,Wire.Encode(new ConsoleUpdateJournal("old-console",Path.Combine(install.InstallRoot,"new"),backup,"0.2.0",false)));
        await engine.Recover();Assert.Equal("saved history",File.ReadAllText(Path.Combine(install.DataDirectory,"valert.db")));Assert.Equal("old-console",host.Command);
    }
    [Fact] public async Task InterruptedSnapshotRestartsOriginalWithoutUsingPartialBackup()
    {
        var engine=new ConsoleUpdateEngine(install,host);Directory.CreateDirectory(engine.Operations);
        Security.AtomicWrite(engine.JournalPath,Wire.Encode(new ConsoleUpdateJournal("old-console","unused","incomplete-backup","0.2.0",false,false)));
        await engine.Recover();Assert.Equal("original history",File.ReadAllText(Path.Combine(install.DataDirectory,"valert.db")));Assert.Equal("old-console",host.Command);Assert.False(File.Exists(engine.JournalPath));
    }
    [Theory][InlineData("../escape")][InlineData("0.2.0/evil")][InlineData("https://example.com")]
    public async Task InvalidVersionsCannotSelectPaths(string version){await Assert.ThrowsAsync<InvalidDataException>(()=>new ConsoleUpdateEngine(install,host).Install("unused",version));Assert.Equal(0,host.StopCount);}
    [Fact] public void ZipTraversalIsRejected(){var zipPath=Path.Combine(root,"bad.zip");using(var zip=ZipFile.Open(zipPath,ZipArchiveMode.Create)){using var w=new StreamWriter(zip.CreateEntry("../escape.txt").Open());w.Write("no");}Assert.Throws<InvalidDataException>(()=>ConsoleUpdateEngine.Extract(zipPath,install.InstallRoot));Assert.False(File.Exists(Path.Combine(root,"escape.txt")));}
    sealed class FakeConsoleService:IConsoleService
    {
        public string Command{get;private set;}="old-console";public int StopCount;public bool Ready=true;public Action? OnHealth;
        public Task Stop(){StopCount++;return Task.CompletedTask;}public Task SetCommand(string command){Command=command;return Task.CompletedTask;}public Task Start()=>Task.CompletedTask;
        public Task<bool> Healthy(string version){OnHealth?.Invoke();return Task.FromResult(Ready);}public Task ProtectDirectory(string d)=>Task.CompletedTask;public Task RestoreDataAccess(string d)=>Task.CompletedTask;
    }
}
