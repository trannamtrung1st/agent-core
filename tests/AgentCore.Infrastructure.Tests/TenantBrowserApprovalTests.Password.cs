using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private const string PasswordArguments = """{"credentialRef":"account","target":{"by":"label","value":"Password"}}""";
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_password_Review_uses_compound_scopes_and_exact_bound_secret_then_never_replays(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true); var probe = new EffectProbe(Probe(s)); var service = Service(f, probe);
        var request = (await Request(service, s, ToolCatalog.BrowserFillCredential, PasswordArguments)).Request!; Assert.NotNull(request);
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("credentials.use", request.Permission); Assert.Equal("credential", request.ResourceType);
        Assert.Equal(s.Password!.Credential.Value.CredentialId.ToString("D"), request.ResourceId); Assert.NotNull(request.Operation.BrowserCommand!.Credential);
        Assert.DoesNotContain("tenant-bound-private-password", JsonSerializer.Serialize(request)); Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(request));
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.True(view.Review!.CanDecide);
        Assert.Contains(view.Review.Changes, c => c.Field == "browserArguments" && c.After!.ToString()!.Contains("Browser account", StringComparison.Ordinal));
        // Counts/paging enforce Browser scope even though the primary target is a Credential.
        Assert.Equal(0, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(), allowedCredentials: [s.Password.Credential.Value.CredentialId],
            browserVisibility: new([], null, null))).Total);
        request = await service.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await service.DispatchPendingAsync(); var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var effects = Effects(f, probe); var result = await ExecuteEffect(effects, s with { Run = run }, request);
        Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.Equal(1, probe.PasswordResolutions); Assert.Equal(1, probe.NativeEffects);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        Assert.Equal(result.Receipt, (await ExecuteEffect(Effects(f, probe, reopened), s with { Run = run }, request)).Receipt);
        Assert.Equal(1, probe.PasswordResolutions); Assert.Equal(1, probe.NativeEffects);
        Assert.Null(await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.DoesNotContain("tenant-bound-private-password", JsonSerializer.Serialize(await reopened.ReadAuditAsync(s.Tenant, 200)));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_password_missing_Credential_scope_fails_before_native_target_or_secret_resolution(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true); var target = Probe(s);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.CredentialUse!.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, target), s, ToolCatalog.BrowserFillCredential, PasswordArguments).AsTask());
        Assert.Equal(0, target.Reads);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_password_rotation_invalidates_exact_pending_consent_but_retains_cancellation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true); var target = Probe(s); var service = Service(f, target);
        var request = (await Request(service, s, ToolCatalog.BrowserFillCredential, PasswordArguments)).Request!;
        await f.Service.ReplaceAsync(s.Human.PrincipalId, s.Tenant, s.Password!.Credential.Value.CredentialId, "rotated-private-password",
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Password.Credential.Value.Revision);
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human); Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        Assert.Equal("credential_binding_changed", view.Review.DecisionUnavailableReason);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision).AsTask());
        await service.CancelAsync(s.Tenant, request.RequestId, s.Human, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, request.Revision);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
    }
}
