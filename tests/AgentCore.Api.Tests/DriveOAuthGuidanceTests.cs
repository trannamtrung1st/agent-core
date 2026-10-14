using AgentCore.Contracts.Http;
using System.Net.Http.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;
public sealed class DriveOAuthGuidanceTests
{
    [Fact]
    public async Task Unconfigured_account_and_resolve_reference_canonical_GoogleOAuth()
    {
        using var host = new AgentCoreApiFactory(); var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var response = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 27 });
        response.EnsureSuccessStatusCode();
        var owner = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("instanceId").GetGuid();
        var credentials = host.Services.GetRequiredService<CredentialService>();
        var account = await credentials.CreateGoogleDriveAccountAsync("Unconfigured");
        await credentials.BindAsync(owner, account.CredentialId, "work", 1);
        var drive = new DriveService(host.Services.GetRequiredService<ICredentialStore>(), host.Services.GetRequiredService<IAgentCredentialBindingStore>(),
            host.Services.GetRequiredService<IAgentInstanceStore>(), new Unconfigured(), host.Services.GetRequiredService<IDriveOperationStore>(), TimeProvider.System);
        var view = Assert.Single(await drive.AccountsAsync(owner, null, default));
        Assert.Equal("NotConfigured", view.Status); Assert.False(view.Available);
        Assert.Equal("Ask the operator to configure GoogleOAuth.", view.Recovery);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => drive.ResolveAsync(owner, "work", null, false, default).AsTask());
        Assert.Equal("drive_oauth_not_configured", error.Code); Assert.Equal(403, error.StatusCode);
        Assert.Equal("Ask the operator to configure GoogleOAuth.", error.Message);
    }
    private sealed class Unconfigured : IGoogleDriveProviderFactory
    {
        public bool Enabled => true; public bool Configured => false; public bool Synthetic => true;
        public IGoogleDriveProvider Create(Guid id, long epoch) => throw new InvalidOperationException("Provider must not be created before configuration.");
    }
}
