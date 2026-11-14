using System.IO.Compression;

namespace Valert.Core;

public sealed record ConsoleInstall(string InstallRoot, string DataDirectory);
public sealed record ConsoleUpdateRequest(string Id, string Version);
public sealed record ConsoleUpdateStatus(string Phase, string Message, string? Version, DateTimeOffset Time);
public sealed record ConsoleUpdateJournal(string OldCommand, string Target, string Backup, string Version, bool Committed, bool BackupReady=true);
public interface IConsoleService
{
    string Command { get; }
    Task Stop();
    Task SetCommand(string command);
    Task Start();
    Task<bool> Healthy(string version);
    Task ProtectDirectory(string directory);
    Task RestoreDataAccess(string directory);
}

// The caller supplies a package independently verified against the fixed GitHub repository.
// No write is accepted by the candidate portal until its health check commits this journal.
public sealed class ConsoleUpdateEngine(ConsoleInstall install, IConsoleService service)
{
    public string Operations => Path.Combine(install.InstallRoot,"console-update");
    public string JournalPath => Path.Combine(Operations,"journal.json");
    public void Status(string phase,string message,string? version=null) => Security.AtomicWrite(Path.Combine(install.DataDirectory,"console-update-status.json"),Wire.Encode(new ConsoleUpdateStatus(phase,message,version,DateTimeOffset.UtcNow)));
    public async Task Install(string package, string version)
    {
        if (!GitHubReleases.ValidVersion(version)) throw new InvalidDataException("Invalid version.");
        Directory.CreateDirectory(Operations); await service.ProtectDirectory(Operations);
        await Recover();
        var target = Path.Combine(install.InstallRoot,"portal-versions",version+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target); Extract(package,target);
        var executable = Path.Combine(target,"Valert.Portal.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(target,"wwwroot","index.html"))) throw new InvalidDataException("The release does not contain a complete console.");
        Security.AtomicWrite(Path.Combine(target,"console-install.json"),Wire.Encode(install));
        Security.AtomicWrite(Path.Combine(target,"update-hold"),"Do not accept writes until the privileged updater commits startup validation.");
        var backup=Path.Combine(Operations,"backups",Guid.NewGuid().ToString("N"));
        var oldCommand=service.Command;
        Status("Installing","Stopping the console and preserving its database, settings, and keys.",version);
        var journal=new ConsoleUpdateJournal(oldCommand,target,backup,version,false,false);
        Security.AtomicWrite(JournalPath,Wire.Encode(journal));
        try
        {
            await service.Stop();
            CopyData(install.DataDirectory,backup);
            journal=journal with {BackupReady=true};
            Security.AtomicWrite(JournalPath,Wire.Encode(journal));
            await service.SetCommand("\""+executable+"\""); await service.Start();
            if (!await service.Healthy(version)) throw new InvalidOperationException("The updated console did not pass its startup check.");
            // Once committed, recovery must never restore an older database over accepted reports.
            Security.AtomicWrite(JournalPath,Wire.Encode(journal with { Committed=true }));
            File.Delete(Path.Combine(target,"update-hold"));
            Status("Installed","Console update installed. Database, accounts, MFA, and agent identities were preserved.",version);
            File.Delete(JournalPath);
        }
        catch
        {
            if (File.Exists(JournalPath)) await Recover(); else await service.Start();
            throw;
        }
    }
    public async Task Recover()
    {
        if (!File.Exists(JournalPath)) return;
        var journal=Wire.Decode<ConsoleUpdateJournal>(File.ReadAllText(JournalPath));
        if (journal.Committed)
        {
            File.Delete(Path.Combine(journal.Target,"update-hold")); await service.Start();
            Status("Installed","Completed an interrupted update after successful startup validation.",journal.Version);
        }
        else if(!journal.BackupReady)
        {
            // An interruption while copying the backup has not modified the original data.
            await service.SetCommand(journal.OldCommand);await service.Start();
            Status("RolledBack","The snapshot was interrupted. The original console was restarted with its unchanged data.",journal.Version);
        }
        else
        {
            await service.Stop();
            // Prepare a complete replacement beside the data directory, then rename it atomically.
            // Do not write through service-writable subdirectories as SYSTEM.
            var restore=install.DataDirectory+"-restore-"+Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(restore);await service.ProtectDirectory(restore);CopyData(journal.Backup,restore);
            var failed=install.DataDirectory+"-failed-"+Guid.NewGuid().ToString("N");
            Directory.Move(install.DataDirectory,failed);
            try { Directory.Move(restore,install.DataDirectory); }
            catch { Directory.Move(failed,install.DataDirectory);throw; }
            await service.ProtectDirectory(failed);await service.RestoreDataAccess(install.DataDirectory);
            await service.SetCommand(journal.OldCommand);await service.Start();
            Status("RolledBack","Startup failed or was interrupted. The previous console and its complete data snapshot were restored. Agents will resend unacknowledged reports.",journal.Version);
        }
        File.Delete(JournalPath);
    }
    static readonly HashSet<string> Excluded = ["release-cache","console-update-request.json","console-update-status.json"];
    static void CopyData(string source,string target)
    {
        if ((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked data directories cannot be upgraded automatically.");
        Directory.CreateDirectory(target);
        foreach(var path in Directory.EnumerateFileSystemEntries(source))
        {
            var name=Path.GetFileName(path);if(Excluded.Contains(name))continue;
            var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked data files cannot be upgraded automatically.");
            if((attributes&FileAttributes.Directory)!=0)CopyData(path,Path.Combine(target,name));else File.Copy(path,Path.Combine(target,name),false);
        }
    }
    public static void Extract(string package,string target)
    {
        using var zip=ZipFile.OpenRead(package);long expanded=0;
        foreach(var entry in zip.Entries)
        {
            expanded+=entry.Length;if(expanded>1_500_000_000||entry.FullName.Contains(':'))throw new InvalidDataException("Invalid update archive.");
            var destination=Path.GetFullPath(Path.Combine(target,entry.FullName));
            if(!destination.StartsWith(Path.GetFullPath(target)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsafe update archive path.");
            if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(destination);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);entry.ExtractToFile(destination,false);
        }
    }
}
