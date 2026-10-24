using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private static TenantCredentialRuntimeReadService CredentialReads(Fixture f, ITenantCredentialBindingStore? bindings = null) => new(Operations(f, f.Iam), (ITenantRunStore)f.Iam,
        (ITenantSessionStore)f.Iam, bindings ?? (ITenantCredentialBindingStore)f.Iam, f.RunConfiguration(), f.Iam, new(f.Iam), f.Clock, (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam);
    private sealed class CredentialBindingReadProbe(ITenantCredentialBindingStore inner) : ITenantCredentialBindingStore
    {
        public int Reads;
        public Func<Task>? AfterFirstRead;
        public async ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenant, Guid instance, CancellationToken ct = default)
        {
            var result = await inner.ReadTenantCredentialBindingsAsync(tenant, instance, ct);
            if (++Reads == 1 && AfterFirstRead is { } after) await after();
            return result;
        }
        public ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long revision, AgentCore.Domain.Credentials.AgentCredentialBinding binding,
            long instanceRevision, long credentialRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => throw new InvalidOperationException("Reads cannot bind credentials.");
        public ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long revision, Guid instance, Guid binding, long instanceRevision,
            long bindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => throw new InvalidOperationException("Reads cannot unbind credentials.");
    }

    [Theory] [InlineData(false, "before")] [InlineData(true, "before")] [InlineData(false, "after")] [InlineData(true, "after")]
    [InlineData(false, "history")] [InlineData(true, "history")]
    public async Task Tenant_Credential_discovery_and_retained_metadata_require_current_independent_Session_read_policy(bool sqlite, string stage)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true, credentialUseAllowed: false);
        var probe = new CredentialBindingReadProbe((ITenantCredentialBindingStore)f.Iam); var reads = CredentialReads(f, probe);
        async Task Review() => await RequireReadReview(f, s, "sessions.read");
        if (stage == "history")
        {
            var text = await reads.InspectAsync(Frame(s), "{}", 32768, CancellationToken.None);
            Assert.Contains("Browser account", text); var readCount = probe.Reads;
            var call = new ModelToolCall("credential-list", ToolCatalog.CredentialsList, "{}");
            var messages = new ModelMessage[] { new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name) };
            var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
                new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation,
                    new(AgentRunToolCallCheckpoint.Write(messages), 1, text.Length, 10000, 100), null), f.Ids.NewId());
            await Review();
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(run).AsTask());
            Assert.Equal("credential_read_review_required", denied.ReasonCode); Assert.Equal(readCount, probe.Reads);
            Assert.Null(await new TenantRunExecutionAuthority(Operations(f, f.Iam), f.Iam, f.Clock, f.RunConfiguration(), credentialReads: reads).CurrentDefinitionAsync(run));
        }
        else
        {
            if (stage == "before") await Review(); else probe.AfterFirstRead = Review;
            if (stage == "after")
            {
                var raced = await Assert.ThrowsAsync<SecurityConflictException>(() => reads.InspectAsync(Frame(s), "{}", 32768, CancellationToken.None).AsTask());
                Assert.Equal("stale_revision", raced.ReasonCode); Assert.Equal(1, probe.Reads);
            }
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(Frame(s), "{}", 32768, CancellationToken.None).AsTask());
            Assert.Equal("credential_read_review_required", denied.ReasonCode); Assert.Equal(stage == "before" ? 0 : 1, probe.Reads);
        }
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Status);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Items);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Credential_discovery_requires_only_current_read_and_retained_metadata_never_bypasses_later_scope_or_policy(bool sqlite, bool policy)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var role = state.Roles.Single(r => r.RoleId == s.CredentialUse!.RoleId);
        await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, role.RoleId, role.Name, ["credentials.read"], state.Tenant.Revision);
        var reads = CredentialReads(f); var text = await reads.InspectAsync(Frame(s), "{}", 32768, CancellationToken.None);
        using var output = JsonDocument.Parse(text); Assert.Equal("account", Assert.Single(output.RootElement.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
        Assert.True(output.RootElement.GetProperty("tenantCredentialRead").GetBoolean()); Assert.DoesNotContain("tenant-bound-private-password", text); Assert.DoesNotContain("ProtectedPayload", text);
        var small = await reads.InspectAsync(Frame(s), "{}", 64, CancellationToken.None); Assert.Equal("output_limit", JsonDocument.Parse(small).RootElement.GetProperty("error").GetString());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(Frame(s) with { SessionId = Guid.NewGuid() }, "{}", 32768, CancellationToken.None).AsTask());
        var call = new ModelToolCall("credential-list", ToolCatalog.CredentialsList, "{}");
        var messages = new ModelMessage[] { new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name) };
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write(messages), 1, text.Length, 10000, 100), null), f.Ids.NewId());
        await reads.RequireStoredAsync(run);
        if (policy) await RequireReadReview(f, s, "credentials.read");
        else await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.CredentialUse!.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.RequireStoredAsync(run).AsTask());
        var authority = new TenantRunExecutionAuthority(Operations(f, f.Iam), f.Iam, f.Clock, f.RunConfiguration(), credentialReads: reads);
        Assert.Null(await authority.CurrentDefinitionAsync(run));
        if (policy)
        {
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => reads.InspectAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), "{}", 32768, CancellationToken.None).AsTask());
            Assert.Equal("credential_read_review_required", denied.ReasonCode);
        }
        else
        {
            var hidden = await reads.InspectAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), "{}", 32768, CancellationToken.None);
            Assert.Empty(JsonDocument.Parse(hidden).RootElement.GetProperty("items").EnumerateArray()); Assert.DoesNotContain("Browser account", hidden);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_password_lineage_requires_the_actual_same_Chat_human_root_and_cannot_trust_an_unresolved_parent(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true); var probe = new EffectProbe(Probe(s));
        (s, _) = await BrowserModelApproval(f, s, probe); Assert.NotNull(s.Run.Admission.ApprovalResume);
        await TenantBrowserCredentialRules.RequireChatLineageAsync(s.Run, (id, token) => ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, id, token), CancellationToken.None);
        await Assert.ThrowsAsync<AccessDeniedException>(() => TenantBrowserCredentialRules.RequireChatLineageAsync(s.Run,
            (_, _) => ValueTask.FromResult<AgentRun?>(null), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => TenantBrowserCredentialRules.RequireChatLineageAsync(s.Run,
            (_, _) => ValueTask.FromResult<AgentRun?>(s.Run), CancellationToken.None).AsTask());
    }
}
