using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class BrowserPrivacyApiTests
{
    private const string Path = "/api/v2/admin/browser/privacy";
    [Fact]
    public async Task Owner_authorization_confirmation_exact_origins_conflicts_and_audit_are_enforced()
    {
        await using var factory = new PrivacyFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PutAsJsonAsync(Path,new { })).StatusCode);
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var initial = (await client.GetFromJsonAsync<AdminBrowserPrivacyResponse>(Path))!;
        Assert.Equal("Protected", initial.Effective.Mode); Assert.Equal(0,initial.Saved.Revision);
        Assert.False(initial.Durable); Assert.Contains("ephemeral",initial.Activation);
        var unmasked = new AdminSaveBrowserPrivacyRequest(0,"Unmasked",["https://example.test"],[],false);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(Path,unmasked)).StatusCode);
        foreach (var origin in new[] { "*", "https://example.test/path", "https://user@example.test", "https://example.test?q=secret", "https://example.test#secret", "https://*.test", "file:///", "https://example.test/%2e%2e" })
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(Path,unmasked with { UnmaskedOrigins=[origin], AcknowledgeExposure=true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync(Path,unmasked with { UnmaskedOrigins=["https://different.test"], AcknowledgeExposure=true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(Path,unmasked with { Mode="0", AcknowledgeExposure=true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(Path,unmasked with { ExpectedRevision=long.MaxValue, AcknowledgeExposure=true })).StatusCode);
        var response = await client.PutAsJsonAsync(Path,unmasked with { AcknowledgeExposure=true }); response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<AdminBrowserPrivacyResponse>())!;
        Assert.Equal("Unmasked",saved.Saved.Mode); Assert.Equal("Protected",saved.Effective.Mode); Assert.True(saved.RestartRequired);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync(Path,unmasked with { AcknowledgeExposure=true })).StatusCode);
        var reloaded = (await client.GetFromJsonAsync<AdminBrowserPrivacyResponse>(Path))!;
        response = await client.PutAsJsonAsync(Path,new AdminSaveBrowserPrivacyRequest(reloaded.Saved.Revision,"Disabled",[],[],false)); response.EnsureSuccessStatusCode();
        Assert.Equal("Disabled",(await response.Content.ReadFromJsonAsync<AdminBrowserPrivacyResponse>())!.Saved.Mode);
        var audit = await client.GetStringAsync("/api/v2/admin/events?targetType=browserPrivacy&targetId=host");
        Assert.Contains("BrowserPrivacyChanged",audit); Assert.DoesNotContain("example.test",audit);
    }
    [Theory]
    [InlineData(false,false,"Protected")]
    [InlineData(true,false,"Unmasked")]
    public async Task Deployment_prohibitions_cannot_be_expanded_by_owner(bool capture, bool unmasked, string mode)
    {
        await using var factory = new PrivacyFactory(capture,unmasked);
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync(Path,
            new AdminSaveBrowserPrivacyRequest(0,mode,mode=="Unmasked"?["https://example.test"]:[],[],true))).StatusCode);
    }
    private sealed class PrivacyFactory(bool capture=true,bool unmasked=true) : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.RemoveAll<BrowserPrivacyAuthority>();
                services.AddSingleton(new BrowserPrivacyAuthority(capture,unmasked,["https://example.test"],[]));
            });
        }
    }
}
