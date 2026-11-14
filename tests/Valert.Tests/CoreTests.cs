using System.Security.Cryptography;
using System.Text.Json;
using Valert.Agent;
using Valert.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Valert.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData(11,"Success")][InlineData(10,"Warning")][InlineData(5.1,"Warning")][InlineData(5,"Failed")][InlineData(0,"Failed")]
    public void CapacityBoundaries(double free,string expected)=>Assert.Equal(expected,Health.Capacity(free,100));
    [Fact] public void UnknownCapacityIsNotHealthy(){Assert.Equal("Unknown",Health.Capacity(null,100));Assert.Equal("Unknown",Health.Capacity(0,0));}
    [Fact] public void StaleAtFortyFiveMinutes(){var now=DateTimeOffset.UtcNow;Assert.False(Health.IsStale(now.AddMinutes(-44),now));Assert.True(Health.IsStale(now.AddMinutes(-45),now));}
    [Fact] public void ManualAndDisabledJobsNeverOverdue(){var now=DateTimeOffset.UtcNow;var j=new JobInfo{Id="j",Name="J",Scheduled=false};Assert.False(Health.IsOverdue(j,now.AddDays(-5),30,now));Assert.False(Health.IsOverdue(j with{Scheduled=true,Enabled=false},now.AddDays(-5),30,now));}
    [Fact] public void OverdueFollowsExpectedRunAndGrace(){var now=DateTimeOffset.UtcNow;var j=new JobInfo{Id="j",Name="J",Scheduled=true};Assert.False(Health.IsOverdue(j,now.AddMinutes(-29),30,now));Assert.True(Health.IsOverdue(j,now.AddMinutes(-31),30,now));Assert.False(Health.IsOverdue(j with{Running=true},now.AddDays(-2),30,now));}
    [Fact] public void AdvancedNextRunDoesNotEraseMissedDeadline(){var now=DateTimeOffset.UtcNow;var old=new JobInfo{Id="j",Name="J",Scheduled=true,ScheduleFingerprint="same",NextRun=now.AddHours(-2),LastStart=now.AddDays(-2)};var incoming=old with{NextRun=now.AddDays(1)};Assert.Equal(old.NextRun,Health.CarryDue(old,old.NextRun,incoming,now));Assert.Equal(incoming.NextRun,Health.CarryDue(old,old.NextRun,incoming with{LastStart=now},now));}
    [Fact] public void ScheduleChangeReplacesPriorExpectation(){var now=DateTimeOffset.UtcNow;var old=new JobInfo{Id="j",Name="J",Scheduled=true,ScheduleFingerprint="old"};var current=old with{ScheduleFingerprint="new",NextRun=now.AddDays(7)};Assert.Equal(current.NextRun,Health.CarryDue(old,now.AddDays(-1),current,now));}
    [Fact] public void WeeklyScheduleDoesNotBecomeDailyOverdue(){var s=JsonDocument.Parse("""{"runAutomatically":true,"daily":{"isEnabled":true,"dailyKind":"SelectedDays","days":["Monday"],"localTime":"02:00"}}""").RootElement;var due=VeeamCollector.PreviousDue(s,Health.Chicago,DateTimeOffset.Parse("2026-11-14T18:00:00Z"));Assert.Equal(DateTimeOffset.Parse("2026-11-09T08:00:00Z"),due);}
    [Fact] public void MonthlyScheduleHonorsMonthAndDate(){var s=JsonDocument.Parse("""{"runAutomatically":true,"monthly":{"isEnabled":true,"dayNumberInMonth":"OnDay","dayOfMonth":10,"months":["November"],"localTime":"01:00"}}""").RootElement;Assert.Equal(DateTimeOffset.Parse("2026-11-10T07:00:00Z"),VeeamCollector.PreviousDue(s,Health.Chicago,DateTimeOffset.Parse("2026-11-14T18:00:00Z")));}
    [Theory][InlineData("2026-07-01T04:30:00Z","2026-06-30")][InlineData("2026-12-01T05:30:00Z","2026-11-30")]
    public void ChicagoDaysHonorDaylightSaving(string input,string day)=>Assert.Equal(day,Health.Day(DateTimeOffset.Parse(input)));
    [Fact] public void TotpMatchesRfc6238Vector(){const string secret="GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";Assert.Equal("94287082",Security.Totp(secret,59/30,8));var now=DateTimeOffset.FromUnixTimeSeconds(59);var code=Security.Totp(secret,1);Assert.Equal(1,Security.VerifyTotp(secret,code,now));Assert.Null(Security.VerifyTotp(secret,code,now,1));}
    [Fact] public void PasswordsAreSaltedAndVerified(){var a=Security.Password("long-enough-password");var b=Security.Password("long-enough-password");Assert.NotEqual(a,b);Assert.True(Security.VerifyPassword("long-enough-password",a));Assert.False(Security.VerifyPassword("wrong",a));}
    [Fact] public void MigrationArchiveAuthenticatesItsContents(){var encrypted=Security.EncryptArchive("important state"u8.ToArray(),"long migration passphrase");Assert.Equal("important state"u8.ToArray(),Security.DecryptArchive(encrypted,"long migration passphrase"));Assert.ThrowsAny<CryptographicException>(()=>Security.DecryptArchive(encrypted,"incorrect passphrase"));encrypted[^1]^=1;Assert.ThrowsAny<CryptographicException>(()=>Security.DecryptArchive(encrypted,"long migration passphrase"));}
    [Fact] public void UpdateSignatureRejectsChangedUrlHashAndVersion(){using var rsa=RSA.Create(2048);var unsigned=new UpdateManifest("0.1.1","https://portal.example/api/agents/package/0.1.1","abc",42,"");var signed=unsigned with{Signature=Convert.ToBase64String(rsa.SignData(Security.ManifestBytes(unsigned),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))};var key=rsa.ExportSubjectPublicKeyInfoPem();Assert.True(Security.VerifyManifest(signed,key));Assert.False(Security.VerifyManifest(signed with{Url="https://attacker.example/payload"},key));Assert.False(Security.VerifyManifest(signed with{Sha256="def"},key));Assert.False(Security.VerifyManifest(signed with{Version="0.1.2"},key));}
    [Fact] public void QueueDeletesOnlyAfterMatchingAcknowledgement(){var path=Path.Combine(Path.GetTempPath(),"valert-queue-"+Guid.NewGuid());try{var q=new DurableQueue(path);var report=new AgentReport();q.Add(report);var file=Assert.Single(q.Files());Assert.Throws<InvalidDataException>(()=>q.Acknowledge(file,report.ReportId,new("wrong",true,null,false)));Assert.True(File.Exists(file));q.Acknowledge(file,report.ReportId,new(report.ReportId,true,null,false));Assert.Empty(q.Files());}finally{Directory.Delete(path,true);}}
    [Theory][InlineData("0.1.0-preview.2","0.1.0-preview.1",true)][InlineData("0.1.0","0.1.0-preview.4",true)][InlineData("0.1.0-preview.5","0.1.0",false)]
    public void ReleaseVersionsRespectPreviewOrdering(string a,string b,bool newer)=>Assert.Equal(newer,Health.Newer(a,b));
}
