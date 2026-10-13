using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class EmailMonitoringApiTests
{
    [Fact]
    public async Task Owner_only_saved_CAS_check_coalescing_and_host_off_are_safe()
    {
        await using var factory = new AgentCoreApiFactory(); using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var account = await factory.Services.GetRequiredService<CredentialService>().CreateEmailAccountAsync("Watch API", EmailAccountProfile.Custom("api@example.test"), "private-fixture");
        var path = $"/api/v2/admin/email-accounts/{account.CredentialId}/monitoring";
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, new AdminEmailMonitoringRequest(0, true, 300))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path + "/check", new { expectedRevision = 0 })).StatusCode);
        var read = await client.GetAsync(path); read.EnsureSuccessStatusCode(); Assert.Contains("no-store", read.Headers.CacheControl!.ToString());
        var initial = (await read.Content.ReadFromJsonAsync<EmailMonitoringView>())!; Assert.False(initial.Enabled); Assert.Equal(0, initial.ConfigurationRevision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/check", new { expectedRevision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new AdminEmailMonitoringRequest(0, true, 120))).StatusCode);
        var saved = await client.PutAsJsonAsync(path, new AdminEmailMonitoringRequest(0, true, 60)); saved.EnsureSuccessStatusCode();
        Assert.Equal(1, (await factory.Services.GetRequiredService<ICredentialStore>().GetAsync(account.CredentialId))!.Revision);
        var payload = await saved.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "private-fixture", "cursor", "lease", "fingerprint", "protectedPayload" }) Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, new AdminEmailMonitoringRequest(0, false, 300))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(path + "/check", new { expectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(path + "/check", new { expectedRevision = 1 })).StatusCode);
        factory.Services.GetRequiredService<EmailMonitoringOptions>().Enabled = false;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/check", new { expectedRevision = 1 })).StatusCode);
        Assert.Equal("HostDisabled", (await client.GetFromJsonAsync<EmailMonitoringView>(path))!.Status);
        (await client.PutAsJsonAsync(path, new AdminEmailMonitoringRequest(1, false, 900))).EnsureSuccessStatusCode();
        Assert.False((await client.GetFromJsonAsync<EmailMonitoringView>(path))!.Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/admin/email-accounts/{Guid.NewGuid()}/monitoring")).StatusCode);
    }
}
