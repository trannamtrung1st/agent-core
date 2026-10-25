using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Baseline_binding_changes_reject_conflicting_Context_aliases_without_partial_revisions(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var s = await RuntimeCredential(f, true, contextual: true);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var other = await f.Service.CreateAsync(s.Owner.PrincipalId, s.Tenant, Password("Another account", "private-alias-test"), await Revision());
        var before = await Revision();
        var conflict = await Assert.ThrowsAsync<SecurityConflictException>(() => f.BindingService.BindAsync(s.Owner.PrincipalId, s.Tenant,
            s.Agent.Instance.InstanceId, other.Credential.CredentialId, "work-drive", before, s.Agent.Instance.Revision, other.Credential.Revision).AsTask());
        Assert.Equal("contextual_binding_conflict", conflict.ReasonCode); Assert.Equal(before, await Revision());
        Assert.Empty((await ((ITenantCredentialBindingStore)f.Iam).ReadTenantCredentialBindingsAsync(s.Tenant, s.Agent.Instance.InstanceId))!.Bindings);
        var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
        Assert.Equal(s.Agent.Instance.Revision, current.Instance.Revision);
        var original = (await f.Credentials.FindTenantCredentialAsync(s.Tenant, s.CredentialId))!.Value;
        var rename = await Assert.ThrowsAsync<SecurityConflictException>(() => f.BindingService.BindAsync(s.Owner.PrincipalId, s.Tenant,
            s.Agent.Instance.InstanceId, s.CredentialId, "renamed-global", before, s.Agent.Instance.Revision,
            original.Revision).AsTask());
        Assert.Equal("contextual_binding_conflict", rename.ReasonCode); Assert.Equal(before, await Revision());
        var member = Assert.Single(await ((ITenantSpaceStore)f.Iam).ReadMembershipsAsync(s.Tenant, s.Run.Admission.Configuration!.SpaceContext!.SpaceId));
        Assert.Equal("work-drive", Assert.Single(member.AddedBindings, b => b.Kind == SpaceBindingKind.Credential).AccountRef);
    }
}
