using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Canonical_private_Definition_Skill_activation_uses_exact_pinned_publication_identity_and_independent_owned_child_reads(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "private-definition-skill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        const string key = "definition:browser.record.lookup";
        try
        {
            var model = new PrivateSkillModel(false, key: key);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = "Disabled" } } })).StatusCode);
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = Assert.Single(state.Users).PrincipalId;
            var access = host.Services.GetRequiredService<TenantAccessService>(); var role = await access.SaveRoleAsync(human, tenant, null, "One inherited procedure", ["skills.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "skill", instance.ToString("D") + ":" + key), await Revision(owner, tenant));
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 }))).GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Load the exact definition:browser.record.lookup procedure; perform no browser effect.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var id = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var run = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, id);
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            var pin = Assert.Single(run.PinnedSkillCatalog, s => s.Key == key); Assert.Equal("32", pin.SourceRevision);
            Assert.Contains(key, run.ActiveSkillKeys); Assert.Equal(1, run.SkillLoadCount);
            Assert.DoesNotContain(pin.Procedure, model.Requests.First().Context!.Text(ContextSectionId.Skills));
            Assert.Contains(pin.Procedure, model.Requests.Last().Context!.Text(ContextSectionId.Skills));
            var retained = Assert.Single(TenantManagementReadReferences.Read(run.Checkpoint));
            Assert.Equal(key, Assert.Single(TenantManagementInspectionSources.Read(retained.SourcesJson)).Key);
            Assert.Equal(3, run.Admission.Configuration!.InstanceRevision); Assert.Equal(3, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
