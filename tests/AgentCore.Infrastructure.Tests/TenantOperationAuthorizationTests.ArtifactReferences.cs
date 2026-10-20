using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "customer-support", 3)] [InlineData(true, "customer-support", 3)]
    [InlineData(false, "general-assistant", 32)] [InlineData(true, "general-assistant", 32)]
    public async Task Artifact_reference_preflight_requires_actual_source_current_claim_and_independent_read_not_creation(bool sqlite, string template, int version)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, template, version);
        var configuration = f.RunConfiguration();
        if (template == "general-assistant")
        {
            await CancelArtifactRun(f, setup, await ClaimArtifactRun(f, setup));
            configuration = f.RunConfiguration(new ResourceExportOnlyBuiltIns(f.BuiltIns));
            var currentSession = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Run.SessionId))!;
            var admission = await new TenantRunService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, configuration,
                f.Identity, f.Authorizer, AgentCore.Infrastructure.Providers.ModelCatalogFactory.Synthetic(), f.Ids, f.Clock)
                .SubmitTextAsync(setup.Tenant, currentSession.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Source-export-only response", (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, currentSession.Snapshot.Revision);
            setup = setup with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, admission.RunId))! };
            Assert.True(AgentCore.Application.Tools.TenantWorkspaceArtifactContract.Allows(setup.Run.Admission.Configuration!.Definition));
            Assert.False(AgentCore.Application.Tools.TenantArtifactToolContract.Allows(setup.Run.Admission.Configuration.Definition));
            Assert.False(AgentCore.Application.Agents.RolePermissions.AllowsTool(setup.Run.Admission.Configuration.Definition, AgentCore.Application.Tools.ToolCatalog.ArtifactsVerify));
        }
        var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup);
        var store = (ITenantArtifactStore)f.Artifacts;
        var own = await store.CreateTenantAsync(frame, f.Ids.NewId(), "own.md", "text/plain", new byte[] { 1 });
        var second = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var admitted = await f.RunService().SubmitTextAsync(setup.Tenant, second.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Other Session", (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var other = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, admitted.RunId))!;
        var otherFrame = await ClaimArtifactRun(f, setup with { Run = other });
        var foreign = await store.CreateTenantAsync(otherFrame, f.Ids.NewId(), "other.md", "text/plain", new byte[] { 2 });
        var service = new TenantArtifactReferenceService(store, (ITenantSessionStore)f.Iam, Operations(f), configuration, f.Authorizer, f.Clock);
        var bound = service.Bind(frame);
        Assert.False((await bound.PrepareAsync(frame.SessionId, [own.ArtifactId.ToString("D")])).IsAuthorized(frame.SessionId, own.ArtifactId.ToString("D")));
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Independent Artifact read", ["artifacts.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var prepared = await bound.PrepareAsync(frame.SessionId, [own.ArtifactId.ToString("N").ToUpperInvariant(), foreign.ArtifactId.ToString("D"), "fixture-artifact-1"]);
        Assert.True(prepared.IsAuthorized(frame.SessionId, own.ArtifactId.ToString("D")));
        Assert.False(prepared.IsAuthorized(frame.SessionId, foreign.ArtifactId.ToString("D"))); Assert.False(prepared.IsAuthorized(frame.SessionId, "fixture-artifact-1"));
        Assert.False(prepared.IsAuthorized(otherFrame.SessionId, own.ArtifactId.ToString("D")));
        Assert.False((await service.Bind(frame with { Generation = f.Ids.NewId() }).PrepareAsync(frame.SessionId, [own.ArtifactId.ToString("D")])).IsAuthorized(frame.SessionId, own.ArtifactId.ToString("D")));
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.False((await bound.PrepareAsync(frame.SessionId, [own.ArtifactId.ToString("D")])).IsAuthorized(frame.SessionId, own.ArtifactId.ToString("D")));
    }
}
