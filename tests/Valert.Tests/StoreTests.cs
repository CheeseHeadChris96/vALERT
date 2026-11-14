using Valert.Core;
using Valert.Portal;
using Xunit;

namespace Valert.Tests;
public sealed class StoreTests:IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"valert-store-"+Guid.NewGuid());
    readonly Store store;readonly Customer customer;readonly AgentRecord agent;
    public StoreTests(){store=new(path);customer=new(){Name="Test customer"};store.Save(customer);agent=new(){CustomerId=customer.Id,MachineName="VBR01",CredentialHash=Security.Hash("credential")};store.Save(agent);}
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(path,true);}
    AgentReport Report(DateTimeOffset time,string result,string runId="run",string? reportId=null)=>new(){ReportId=reportId??Guid.NewGuid().ToString(),CollectedAt=time,CollectionSucceeded=true,InventoryComplete=true,Jobs=[new(){Id="job",Name="Nightly",Scheduled=true,ScheduleFingerprint="daily",LastStart=time.AddMinutes(-10),NextRun=time.AddDays(1)}],Runs=[new(){Id=runId,JobId="job",StartedAt=time.AddMinutes(-10),EndedAt=time,Result=result,Vms=[new("VM01",result,time.AddMinutes(-10),time,null)]}]};
    [Fact] public void RepeatedReportsAreIdempotent(){var now=DateTimeOffset.UtcNow;var report=Report(now,"Success");store.Ingest(agent,report,now);store.Ingest(agent,report,now);Assert.Single(store.JobViews(customer.Id,now));Assert.Single(store.Read<JobRun>("SELECT payload FROM runs"));}
    [Fact] public void RetryTurnsHeatmapGreenAndKeepsFailure(){var now=DateTimeOffset.Parse(Health.Day(DateTimeOffset.UtcNow)+"T18:00:00Z");if(now>DateTimeOffset.UtcNow)now=DateTimeOffset.UtcNow;store.Ingest(agent,Report(now.AddMinutes(-20),"Failed","failed"),now);store.Ingest(agent,Report(now,"Success","retry"),now);Assert.Equal("Success",Assert.Single(store.Heatmap(customer.Id,30)).Result);Assert.Equal(2,store.Read<JobRun>("SELECT payload FROM runs").Count);Assert.Equal("Success",Assert.Single(store.JobViews(customer.Id,now)).Status);}
    [Fact] public void OldBacklogDoesNotReplaceCurrentSnapshot(){var now=DateTimeOffset.UtcNow;store.Ingest(agent,Report(now,"Success","new"),now);store.Ingest(agent,Report(now.AddDays(-1),"Failed","old"),now);Assert.Equal(now,store.Agent(agent.Id)!.LastGoodCollection);Assert.Equal("Success",Assert.Single(store.JobViews(customer.Id,now)).Status);}
    [Fact] public void FailedCollectionDoesNotRemoveJobs(){var now=DateTimeOffset.UtcNow;store.Ingest(agent,Report(now.AddMinutes(-1),"Success"),now);store.Ingest(agent,new AgentReport{CollectedAt=now,Issues=["Cannot authenticate"]},now);Assert.True(Assert.Single(store.Jobs(customer.Id)).Present);Assert.Equal(now.AddMinutes(-1),store.Agent(agent.Id)!.LastGoodCollection);}
    [Fact] public void RemovedJobRetainsHistoryButLeavesCurrentTotals(){var now=DateTimeOffset.UtcNow;store.Ingest(agent,Report(now.AddMinutes(-1),"Success"),now);store.Ingest(agent,new AgentReport{CollectedAt=now,CollectionSucceeded=true,InventoryComplete=true},now);Assert.False(Assert.Single(store.Jobs(customer.Id)).Present);Assert.Single(store.Read<JobRun>("SELECT payload FROM runs"));}
    [Fact] public void EnrollmentIsCustomerBoundAndSingleUse(){var token=Security.Token();store.Exec("INSERT INTO enrollments VALUES(@p0,@p1,@p2)",Security.Hash(token),customer.Id,DateTimeOffset.UtcNow.AddHours(1).ToString("O"));var result=store.Enroll(new(token,"VBR02","0.1.0"));Assert.Equal(customer.Id,result.CustomerId);Assert.NotNull(store.AuthenticateAgent(result.Credential));Assert.Throws<UnauthorizedAccessException>(()=>store.Enroll(new(token,"VBR03","0.1.0")));}
    [Fact] public void AgentIdsIsolateIdenticalVeeamJobIds(){var now=DateTimeOffset.UtcNow;var other=new AgentRecord{CustomerId=customer.Id,MachineName="VBR02",CredentialHash=Security.Hash("second")};store.Save(other);store.Ingest(agent,Report(now,"Success"),now);store.Ingest(other,Report(now,"Failed"),now);Assert.Equal(2,store.JobViews(customer.Id,now).Count);}
    [Fact] public void VmSuccessUsesAllRetainedHistoryAndSeparatesServers()
    {
        var now=DateTimeOffset.UtcNow;var success=Report(now.AddDays(-2),"Success","success");
        var failures=Enumerable.Range(0,110).Select(i=>new JobRun{Id="failed"+i,JobId="job",StartedAt=now.AddMinutes(-i-1),EndedAt=now.AddMinutes(-i),Result="Failed",Vms=[new("VM01","Failed",now.AddMinutes(-i-1),now.AddMinutes(-i),null)]});
        store.Ingest(agent,success with {Runs=[..success.Runs,..failures]},now);
        var other=new AgentRecord{CustomerId=customer.Id,MachineName="OTHER",CredentialHash=Security.Hash("other")};store.Save(other);store.Ingest(other,Report(now,"Success"),now);
        Assert.Equal(now.AddDays(-2),store.VmLastSuccess(agent.Id,"job")["VM01"]);
        Assert.Equal(now,store.VmLastSuccess(other.Id,"job")["VM01"]);
    }
    [Fact] public void MigrationPreservesIdentityAndHistory(){var now=DateTimeOffset.UtcNow;store.Ingest(agent,Report(now,"Success"),now);var p=new Platform(store);_ = p.PublicKey;var key=p.PublicKey;var bytes=p.Export("correct migration passphrase");Assert.True(p.Settings.MigrationPaused);var target=path+"-restored";var archive=path+".vab";try{File.WriteAllBytes(archive,bytes);Platform.Restore(target,archive,"correct migration passphrase");var copy=new Store(target);Assert.Equal(customer.Name,copy.Customer(customer.Id)!.Name);Assert.Equal(agent.Id,copy.AuthenticateAgent("credential")!.Id);Assert.Single(copy.Read<JobRun>("SELECT payload FROM runs"));Assert.Equal(key,new Platform(copy).PublicKey);Assert.False(new Platform(copy).Settings.MigrationPaused);}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(target))Directory.Delete(target,true);File.Delete(archive);}}
}
