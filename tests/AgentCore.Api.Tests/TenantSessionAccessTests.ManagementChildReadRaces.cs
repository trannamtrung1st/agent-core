using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> ManagementChildRaceCases =>
        from sqlite in new[] { false, true }
        from tool in new[] { "skills.list", "skills.inspect", "resources.list", "resources.inspect" }
        from boundary in new[] { "revoke", "source_changed" }
        select new object[] { sqlite, tool, boundary };

    [Theory] [MemberData(nameof(ManagementChildRaceCases))]
    public async Task Canonical_management_child_reads_recheck_sources_before_model_reentry_and_protected_output(bool sqlite, string tool, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-child-race-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel(tool); var race = new ManagementReadRace();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                s.Replace(ServiceDescriptor.Singleton<ITenantManagementStore>(p => new ManagementReadHook((ITenantManagementStore)p.GetRequiredService<IIamStore>(), race)));
            }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementInspection(host, owner); var api = $"/api/v2/tenants/{tenant}";
            model.ArgumentsOverride = await ChildReadArguments(owner, tenant, instance, tool);
            Guid session = default; string[]? admittedSkillWitnesses = null;
            race.AfterObservation = async () => {
                var active = Assert.Single((await host.Services.GetRequiredService<ITenantRunStore>()
                    .ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items);
                var prior = TenantManagementReadReferences.Read(active.Checkpoint);
                Assert.Equal(new[] { "skills.load", "skills.load" }, prior.Select(r => r.ToolName));
                Assert.Equal(new[] { "{\"ids\":[\"definition:browser.record.lookup\"]}", "{\"ids\":[\"instance:metadata-private\"]}" },
                    prior.Select(r => r.ArgumentsJson).Order(StringComparer.Ordinal));
                admittedSkillWitnesses = prior.Select(r => JsonSerializer.Serialize(r)).ToArray();
                if (boundary == "revoke")
                {
                    var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
                    var role = state.Roles.Single(r => r.Name == "Compound management metadata reader");
                    var permission = tool.StartsWith("skills.", StringComparison.Ordinal) ? "skills.read" : "resources.read";
                    await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(state.Users.Single().PrincipalId, tenant, role.RoleId, role.Name,
                        new[] { "skills.read", "resources.read", "credentials.read" }.Where(p => p != permission).ToArray(), state.Tenant.Revision);
                }
                else
                {
                    var response = await owner.PostAsJsonAsync(api + $"/agents/{instance}/skills/commands", new { operation = "update", key = "instance:metadata-private", expectedSkillRevision = 1,
                        expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6,
                        input = new { name = "Changed private Skill", description = "Changed under its owner", procedure = "NEW-PRIVATE-PROCEDURE", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } });
                    Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                }
            };
            session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6 }))).GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Read the current child before its authority changes.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            var runId = (await Json(submitted)).GetProperty("runId").GetGuid(); if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var final = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
            Assert.Equal(1, race.Applied); Assert.Equal(AgentRunStatus.Failed, final.Status); Assert.Equal(2, model.Requests.Count);
            Assert.NotNull(admittedSkillWitnesses);
            Assert.Equal(admittedSkillWitnesses, TenantManagementReadReferences.Read(final.Checkpoint).Select(r => JsonSerializer.Serialize(r)));
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Text.Contains("\"tenantManagementRead\":true", StringComparison.Ordinal));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
