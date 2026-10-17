using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    public static IEnumerable<object[]> AgentConfigurationReadRaces =>
        from sqlite in new[] { false, true }
        from read in new[] { "effective", "settings", "budgets", "management", "skills", "resources", "editor" }
        from change in new[] { "persona", "security", "logout" }
        select new object[] { sqlite, read, change };

    [Theory]
    [MemberData(nameof(AgentConfigurationReadRaces))]
    public async Task Human_agent_reads_reject_captured_configuration_after_original_authority_or_revision_changes(bool sqlite, string read, string change)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var gated = ConfigurationServices(f, wrapBuiltIns: inner => new AfterEditorDefinition(inner, async () =>
        {
            if (change == "persona") await configuration.UpdatePersonaAsync(owner.OwnerPrincipalId, owner.TenantId, id, agent.Instance.Persona with { Name = "Concurrent persona" }, await Revision(), 1, 1);
            else if (change == "security") await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Concurrent role", ["agents.read"], await Revision());
            else await f.Identity.SignOutAsync(login.SessionId);
        }));
        var reads = new TenantAgentReadService(f.Iam, (AgentCore.Application.Ports.ITenantAgentStore)f.Iam, gated.Configuration, f.Identity, f.Authorizer, agents);
        async Task Execute()
        {
            switch (read)
            {
                case "effective": await reads.ReadConfigurationAsync(owner.TenantId, id, human); break;
                case "settings": await reads.ReadSettingsAsync(owner.TenantId, id, human); break;
                case "budgets": await reads.ReadBudgetsAsync(owner.TenantId, id, human); break;
                case "management": await reads.ReadManagementAsync(owner.TenantId, id, human); break;
                case "skills": await reads.ReadSkillsAsync(owner.TenantId, id, human); break;
                case "resources": await reads.ReadResourcesAsync(owner.TenantId, id, human); break;
                case "editor": await reads.ReadEditorAsync(owner.TenantId, id, human); break;
                default: throw new InvalidOperationException(read);
            }
        }
        if (change == "logout") Assert.Equal("initiator_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(Execute)).ReasonCode);
        else
        {
            Assert.Equal("stale_revision", (await Assert.ThrowsAsync<SecurityConflictException>(Execute)).ReasonCode);
            await Execute(); // The one-shot interceptor is retired: an explicit current read succeeds.
        }
    }
}
