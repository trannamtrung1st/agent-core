using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Fact]
    public async Task Managed_default_bootstrap_and_new_organization_use_the_same_protected_configuration_and_discovery_paths()
    {
        await using var factory = new AgentCoreApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(client, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await client.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        var first = await Json(await client.GetAsync(root + "/chat/agents?pageSize=1"));
        var candidate = first.GetProperty("items")[0]; Assert.True(candidate.GetProperty("systemManaged").GetBoolean());
        Assert.Equal("General Assistant", candidate.GetProperty("name").GetString());
        var id = candidate.GetProperty("instanceId").GetGuid(); var resource = root + $"/agents/{id}";
        var initial = await Json(await client.GetAsync(resource + "/editor"));
        Assert.Equal("GeneralAssistant", initial.GetProperty("agent").GetProperty("managedKind").GetString());
        var versions = await Json(await client.GetAsync(resource + "/versions")); Assert.False(versions.GetProperty("canAdopt").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(resource + "/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(client, tenant), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(resource + "/active-version", new { version = 35, expectedRevision = await Revision(client, tenant), expectedInstanceRevision = 1 })).StatusCode);
        var hidden = await client.PutAsJsonAsync(resource + "/chat-visibility", new { showInChat = false, expectedRevision = await Revision(client, tenant), expectedInstanceRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var empty = await Json(await client.GetAsync(root + "/chat/agents")); Assert.Equal(0, empty.GetProperty("total").GetInt32()); Assert.Equal("no_visible_agents", empty.GetProperty("emptyReason").GetString());
        var search = await Json(await client.GetAsync(root + "/chat/agents?q=General")); Assert.Equal(0, search.GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await client.GetAsync(root + "/agents"))).GetProperty("total").GetInt32());
        var operation = Guid.NewGuid(); var revision = await Revision(client, tenant);
        var resetBody = new { operationId = operation, expectedRevision = revision, expectedInstanceRevision = 2 };
        var reset = await client.PostAsJsonAsync(resource + "/reset-defaults", resetBody); Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("applied", (await Json(reset)).GetProperty("status").GetString());
        Assert.Equal("already_applied", (await Json(await client.PostAsJsonAsync(resource + "/reset-defaults", resetBody))).GetProperty("status").GetString());
        var editor = await Json(await client.GetAsync(resource + "/editor")); Assert.False(editor.GetProperty("agent").GetProperty("instance").GetProperty("showInChat").GetBoolean());
        var org = await Json(await client.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Managed second", slug = "managed-second" }));
        var second = org.GetProperty("tenantId").GetGuid();
        var inventory = await Json(await client.GetAsync($"/api/v2/tenants/{second}/agents"));
        Assert.Equal(1, inventory.GetProperty("total").GetInt32());
        Assert.NotEqual(id, inventory.GetProperty("items")[0].GetProperty("instance").GetProperty("instanceId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v2/tenants/{second}/agents/{id}/reset-defaults", new { operationId = Guid.NewGuid(), expectedRevision = await Revision(client, second), expectedInstanceRevision = 3 })).StatusCode);
        var store = factory.Services.GetRequiredService<IIamStore>(); var state = (await store.ReadTenantAsync(tenant))!;
        var audit = (await store.ReadAuditAsync(tenant, 200)).Single(a => a.Action == ManagedGeneralAssistant.ProvisionAction);
        Assert.Equal(Guid.Empty, audit.ActorPrincipalId);
        var managed = state.SystemIdentities.Single(i => i.ManagedKind is not null);
        Assert.DoesNotContain(state.Assignments.Where(a => a.PrincipalId == managed.PrincipalId), a => state.Roles.Single(r => r.RoleId == a.RoleId).Key is "owner" or "agent-administrator" or "contributor");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Managed_default_executes_user_research_with_its_starter_rights_and_current_native_tool_boundaries(bool sqlite)
    {
        var native = new PublicResearchPort(); var model = new ManagedResearchModel();
        void Configure(IServiceCollection services) { services.Replace(ServiceDescriptor.Singleton<IWebSearchProvider>(native)); services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(native)); }
        await using var factory = new RuntimeStateFactory(sqlite, model: model, configure: Configure, nativeDefinitions: true);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var chat = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/chat/agents"));
        var instance = chat.GetProperty("items")[0].GetProperty("instanceId").GetGuid();
        var space = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance))!;
        var home = AgentWorkspacePhysicalPaths.SpaceHomeDirectory(factory.Services.GetRequiredService<PersistenceOptions>().WorkspaceRoot, tenant, space.SpaceId);
        Directory.CreateDirectory(home); await File.WriteAllTextAsync(Path.Combine(home, "proof.txt"), "MANAGED-PRIVATE-FILE");
        var submitted = await SubmitProfileRequest(owner, tenant, instance, message: "Research the public source and read my private file.");
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var done = await ProfileTerminal(factory, tenant, submitted.Run);
        Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure}"); Assert.Equal(36, done.DefinitionVersion);
        Assert.Equal(2, native.Calls);
        var results = model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        Assert.Contains(results, m => m.Name == ToolCatalog.WebFetch && m.Text.Contains("AUTOMATION-PUBLIC-SOURCE", StringComparison.Ordinal));
        Assert.Contains(results, m => m.Name == ToolCatalog.WorkspaceRead && m.Text.Contains("MANAGED-PRIVATE-FILE", StringComparison.Ordinal));
        Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
    }
    [Fact]
    public async Task Managed_default_chat_only_member_needs_no_inventory_rights_and_revocation_removes_public_matches()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var root = $"/api/v2/tenants/{tenant}";
        var id = (await Json(await owner.GetAsync(root + "/chat/agents"))).GetProperty("items")[0].GetProperty("instanceId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Assistant Chat only", permissions = new[] { "agents.chat", "sessions.create", "sessions.read" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "managed-viewer@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Csrf(viewer);
        Assert.Equal(HttpStatusCode.Created, (await viewer.PostAsJsonAsync("/api/v2/auth/register", new { login = "managed-viewer@example.test", displayName = "Chat member", password = "explicit-managed-viewer-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(viewer, "managed-viewer@example.test", "explicit-managed-viewer-password");
        Assert.Equal(0, (await Json(await viewer.GetAsync(root + "/agents"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await viewer.GetAsync(root + "/chat/agents?q=General"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(root + $"/agents/{id}/reset-defaults", new { operationId = Guid.NewGuid(), expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
        var store = factory.Services.GetRequiredService<IIamStore>(); var state = (await store.ReadTenantAsync(tenant))!;
        var assignment = state.Assignments.Single(a => a.RoleId == role.GetProperty("roleId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(root + $"/role-assignments/{assignment.AssignmentId}?expectedRevision={state.Tenant.Revision}")).StatusCode);
        var none = await Json(await viewer.GetAsync(root + "/chat/agents?q=General")); Assert.Equal(0, none.GetProperty("total").GetInt32()); Assert.Empty(none.GetProperty("items").EnumerateArray());
    }
    private sealed class ManagedResearchModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, true);
        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            ModelToolCall? call = results.Length switch {
                0 => new("managed-search", ToolCatalog.WebSearch, "{\"query\":\"public source\",\"limit\":1}"),
                1 => new("managed-fetch", ToolCatalog.WebFetch, "{\"url\":\"https://public.example/source\"}"),
                2 => new("managed-file", ToolCatalog.WorkspaceRead, "{\"path\":\"/private/proof.txt\"}"),
                _ => null
            };
            if (call is not null) { yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); }
            else { yield return new ModelTextDelta("Research and file reading completed from observed tool results."); yield return new ModelCompleted(ModelStopReason.Completed); }
        }
    }

}
