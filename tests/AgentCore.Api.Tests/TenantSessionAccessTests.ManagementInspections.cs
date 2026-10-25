using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Contracts.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private static async Task<(Guid Tenant, Guid Instance)> SetupManagementInspection(WebApplicationFactory<Program> host, HttpClient owner, bool childReads = true)
    {
        var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
            overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Instructions", "KnowledgeResources", "ToolSelection", "Skills", "Settings", "Resources", "Connections" } } }
        })).StatusCode);
        var path = api + $"/agents/{instance}";
        var skill = await Json(await owner.PostAsJsonAsync(path + "/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3,
            input = new { id = "metadata-private", name = "Review <script>literal Skill</script>", description = "Scoped private metadata", procedure = "PRIVATE-INSPECTION-PROCEDURE-SENTINEL", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } }));
        var resource = await Json(await owner.PostAsJsonAsync(path + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4,
            input = new { logicalPath = "private/metadata.txt", kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes("PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL") } }));
        var credential = await Json(await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("Inspection credential <literal>", "Password", "General", await Revision(owner, tenant), ProtectedValue: "PRIVATE-INSPECTION-SECRET-SENTINEL")));
        var credentialId = credential.GetProperty("credential").GetProperty("credentialId").GetGuid();
        _ = await Json(await owner.PostAsJsonAsync(path + "/credential-bindings", new TenantBindCredentialRequest(credentialId, "inspection-account", await Revision(owner, tenant), 5, 1)));
        var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var actor = Assert.Single(state.Users).PrincipalId;
        var access = host.Services.GetRequiredService<TenantAccessService>();
        if (childReads)
        {
        var role = await access.SaveRoleAsync(actor, tenant, null, "Compound management metadata reader", ["skills.read", "resources.read", "credentials.read"], await Revision(owner, tenant));
        var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        await access.AssignRoleAsync(actor, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        }
        return (tenant, instance);
    }
    [Theory]
    [InlineData(false, "harness.inspect", false)] [InlineData(true, "harness.inspect", false)]
    [InlineData(false, "harness.inspect", true)] [InlineData(true, "harness.inspect", true)]
    [InlineData(false, "configuration.inspect", false)] [InlineData(true, "configuration.inspect", false)]
    [InlineData(false, "configuration.inspect", true)] [InlineData(true, "configuration.inspect", true)]
    public async Task Canonical_management_inspection_projects_only_current_owned_delegated_metadata_without_secret_or_procedure_content(bool sqlite, string tool, bool childReads)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-inspection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
        var model = new ManagementReadModel(tool);
        await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
        using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var (tenant, instance) = await SetupManagementInspection(host, owner, childReads); var api = $"/api/v2/tenants/{tenant}";
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6 }))).GetProperty("sessionId").GetGuid();
        var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect this Agent's exact current configuration without executing or changing anything.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid(); if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
        var final = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
        Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
        var message = model.Requests.SelectMany(r => r.Messages).Last(m => m.Name == tool);
        Assert.True(message.Text.Contains("\"tenantManagementRead\":true", StringComparison.Ordinal), message.Text);
        using var result = JsonDocument.Parse(message.Text); var data = result.RootElement.GetProperty("observation").GetProperty("data");
        Assert.True(data.GetProperty("metadataOnly").GetBoolean()); if (childReads) Assert.NotEmpty(data.GetProperty("skills").EnumerateArray()); else Assert.Empty(data.GetProperty("skills").EnumerateArray());
        Assert.DoesNotContain("\"procedure\"", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protectedPayload", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        foreach (var secret in new[] { "PRIVATE-INSPECTION-PROCEDURE-SENTINEL", "PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL", "PRIVATE-INSPECTION-SECRET-SENTINEL" }) Assert.DoesNotContain(secret, message.Text);
        if (!childReads) { Assert.DoesNotContain("metadata-private", message.Text); Assert.DoesNotContain("private/metadata.txt", message.Text); Assert.DoesNotContain("Inspection credential", message.Text); }

        if (tool == "harness.inspect") { Assert.Equal(6, data.GetProperty("instanceRevision").GetInt64()); Assert.True(data.GetProperty("changesApplyToNextRun").GetBoolean()); }
        else { Assert.Equal(12, data.GetProperty("settings").GetArrayLength()); Assert.Equal("nextRun", data.GetProperty("changesApplyTo").GetString()); }
        var source = Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint), reference => reference.ToolName == tool); if (childReads) Assert.NotEmpty(TenantManagementInspectionSources.Read(source.SourcesJson)); else Assert.Empty(TenantManagementInspectionSources.Read(source.SourcesJson));
        await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, runId), [source], null);
        Assert.Equal(6, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
        Assert.Empty((await host.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0,100))).Items);
        if (sqlite)
        {
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022080000_TenantManagementMutations"));
            await db.GetService<IMigrator>().MigrateAsync();
            await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant,runId),[source],null);
        }

        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
