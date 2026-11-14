using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Valert.Core;
using Valert.Portal;
using Xunit;

namespace Valert.Tests;
public sealed class PortalFactory:WebApplicationFactory<Program>
{
    public string DataPath{get;}=Path.Combine(Path.GetTempPath(),"valert-api-"+Guid.NewGuid());
    public PortalFactory(){Environment.SetEnvironmentVariable("VALERT_DATA_DIR",DataPath);}
    protected override void ConfigureWebHost(IWebHostBuilder builder){builder.UseEnvironment("Testing");builder.ConfigureServices(services=>{foreach(var d in services.Where(d=>d.ServiceType==typeof(IHostedService)).ToArray())services.Remove(d);});}
    public HttpClient Browser()=>CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new Uri("https://localhost"),HandleCookies=true,AllowAutoRedirect=false});
    protected override void Dispose(bool disposing){base.Dispose(disposing);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Environment.SetEnvironmentVariable("VALERT_DATA_DIR",null);if(Directory.Exists(DataPath))Directory.Delete(DataPath,true);}
}
public sealed class AccessTests
{
    static async Task<JsonElement> Json(HttpResponseMessage r){r.EnsureSuccessStatusCode();return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();}
    static async Task Refresh(HttpClient c){var session=await Json(await c.GetAsync("/api/auth/session"));c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");c.DefaultRequestHeaders.Add("X-CSRF-TOKEN",session.GetProperty("csrf").GetString());}
    static async Task SignInFirst(PortalFactory f,HttpClient c)
    {
        await Refresh(c);var setup=await Json(await c.PostAsJsonAsync("/api/auth/setup",new {token=File.ReadAllText(Path.Combine(f.DataPath,"initial-setup.txt")),name="Administrator",email="admin@example.test",password="strong test password"}));
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/overview")).StatusCode);
        await Json(await c.PostAsJsonAsync("/api/auth/mfa",new {code=Security.Totp(setup.GetProperty("secret").GetString()!,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30)}));await Refresh(c);
    }
    [Fact] public async Task MfaIsMandatoryAndCsrfIsRequired(){using var f=new PortalFactory();using var c=f.Browser();await SignInFirst(f,c);Assert.Equal(HttpStatusCode.OK,(await c.GetAsync("/api/overview")).StatusCode);c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/admin/customers",new {name="Unauthorized request"})).StatusCode);}
    [Fact] public async Task ViewerCannotReadOtherCustomersOrAdminApi()
    {
        using var f=new PortalFactory();using var admin=f.Browser();await SignInFirst(f,admin);
        var a=await Json(await admin.PostAsJsonAsync("/api/admin/customers",new {name="Assigned customer"}));var b=await Json(await admin.PostAsJsonAsync("/api/admin/customers",new {name="Hidden customer"}));
        var invite=await Json(await admin.PostAsJsonAsync("/api/admin/users",new {name="Viewer",email="viewer@example.test",admin=false,allCustomers=false,customerIds=new[]{a.GetProperty("id").GetString()}}));
        var token=invite.GetProperty("activationUrl").GetString()!.Split("#activate=")[1];using var viewer=f.Browser();await Refresh(viewer);
        var activate=await Json(await viewer.PostAsJsonAsync("/api/auth/activate",new {token,password="another strong password"}));await Json(await viewer.PostAsJsonAsync("/api/auth/mfa",new {code=Security.Totp(activate.GetProperty("secret").GetString()!,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30)}));await Refresh(viewer);
        var overview=await Json(await viewer.GetAsync("/api/overview"));Assert.Equal(1,overview.GetProperty("customers").GetArrayLength());Assert.Equal("Assigned customer",overview.GetProperty("customers")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync("/api/customers/"+b.GetProperty("id").GetString())).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync("/api/admin/state")).StatusCode);
    }
    [Fact] public async Task AgentCannotChooseCustomerOrReadDashboard()
    {
        using var f=new PortalFactory();using var c=f.Browser();await SignInFirst(f,c);var store=f.Services.GetRequiredService<Store>();var customer=new Customer{Name="Agent customer"};store.Save(customer);var token=Security.Token();store.Exec("INSERT INTO enrollments VALUES(@p0,@p1,@p2)",Security.Hash(token),customer.Id,DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        using var agent=f.Browser();var response=await Json(await agent.PostAsJsonAsync("/api/agents/enroll",new EnrollmentRequest(token,"SERVER01",Wire.Version)));agent.DefaultRequestHeaders.Authorization=new("Bearer",response.GetProperty("credential").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized,(await agent.GetAsync("/api/overview")).StatusCode);var report=new AgentReport{CollectionSucceeded=true,InventoryComplete=true};Assert.Equal(HttpStatusCode.OK,(await agent.PostAsJsonAsync("/api/agents/reports",report,Wire.Json)).StatusCode);
        f.Services.GetRequiredService<Platform>().Settings.MigrationPaused=true;Assert.Equal(HttpStatusCode.ServiceUnavailable,(await agent.PostAsJsonAsync("/api/agents/reports",new AgentReport(),Wire.Json)).StatusCode);
    }
}
