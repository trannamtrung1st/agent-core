using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    private static TenantApprovalService Approvals(Fixture f) => new(f.Iam, (ITenantApprovalStore)f.Iam, (ITenantAgentStore)f.Iam,
        new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)), f.Authorizer, f.Identity, f.Ids, f.Clock,
        f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)f.Iam);
    private static TenantApprovedOperationService ApprovedEffects(Fixture f, IIamStore? store = null)
    {
        var current = store ?? f.Iam;
        return new(current, (ITenantRunStore)current, (ITenantAgentStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current,
            new(current, (ITenantRunStore)current, (ITenantAgentStore)current, new(current), new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)), f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default);
    }
    private static async Task<(Environment Environment, TenantApprovalRequest Request, AgentRun Claimed)> ApprovedPreference(Fixture f, bool review)
    {
        var setup = await Setup(f, permissions: review, mode: HarnessManagementMode.Assisted);
        var service = Approvals(f);
        var prepared = await service.PreparePreferencesAsync(new(setup.Tenant, setup.Run.AgentRunId), TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(Preferences)), setup.Run.Revision, setup.Run.Claim!.Generation);
        var request = prepared.Request!; Assert.NotNull(request); Assert.Equal(review ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
        await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        await service.DispatchPendingAsync();
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return (setup, request, run);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Approved_preferences_apply_the_saved_typed_change_once_and_replay_after_reopen_without_broadening_authority(bool sqlite, bool review)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ApprovedPreference(f, review); var run = setup.Claimed; var request = setup.Request;
        var result = await ApprovedEffects(f).ExecuteAsync(new(request.TenantId, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome); Assert.Equal(request.RequestId, result.RequestId);
        var target = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, run.AgentInstanceId))!.Instance;
        Assert.Equal("vi", target.SettingsOverrides!.Language!.Value); Assert.Equal(3, target.Revision); Assert.Equal(setup.Environment.Agent.Instance.Persona, target.Persona);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        Assert.Equal(result, await ApprovedEffects(f, reopened).ExecuteAsync(new(request.TenantId, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash));
        Assert.Single(await reopened.ReadAuditAsync(request.TenantId, 200), a => a.Action == "operations.preferences.apply");
        var grant = await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(request.TenantId, request.RequestId);
        if (review) Assert.Null(grant); else { Assert.Equal(OperationGrantStatus.Consumed, grant!.Status); Assert.DoesNotContain((await reopened.ReadTenantAsync(request.TenantId))!.Assignments, a => a.PrincipalId == request.ExecutorPrincipalId); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Freeze_after_approval_revokes_the_exact_preference_grant_and_records_a_stable_denial(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ApprovedPreference(f, false); var request = setup.Request; var run = setup.Claimed;
        await ChangeManagement(f, setup.Environment, setup.Environment.Agent.Instance.HarnessManagement!.Policy with { Frozen = true });
        var result = await ApprovedEffects(f).ExecuteAsync(new(request.TenantId, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome); Assert.Equal("management_unavailable", result.ReasonCode);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId))!.Status);
        Assert.Equal(3, result.InstanceRevision);
        Assert.Null((await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, run.AgentInstanceId))!.Instance.SettingsOverrides?.Language);
    }

    [Theory]
    [InlineData(false, "valid")] [InlineData(true, "valid")]
    [InlineData(false, "arguments")] [InlineData(true, "arguments")]
    [InlineData(false, "kind")] [InlineData(true, "kind")]
    public async Task Resumption_requires_the_pending_preference_intent_to_match_the_saved_operation(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ApprovedPreference(f, false); var run = setup.Claimed; var request = setup.Request;
        var json = boundary == "kind" ? JsonSerializer.Serialize(new { operation = "update_persona", persona = setup.Environment.Agent.Instance.Persona }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : "{\"operation\":\"update_preferences\",\"preferences\":" + (boundary == "arguments" ? Preferences.Replace("vi", "en", StringComparison.Ordinal) : Preferences) + "}";
        var call = new ModelToolCall("pending-preferences", ToolCatalog.RequestAuthorization, json);
        var runtime = (ITenantRuntimeStore)f.Iam;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 0), null), f.Ids.NewId());
        var continuation = new TenantRunContinuationService((ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam, ApprovedEffects(f), f.Clock, runtime, f.Ids);
        if (boundary == "valid")
        {
            var resumed = await continuation.ResumeAsync(run); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(messages!));
        }
        else
        {
            await Assert.ThrowsAsync<SecurityConflictException>(() => continuation.ResumeAsync(run).AsTask());
            Assert.Equal(2, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, run.AgentInstanceId))!.Instance.Revision);
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId));
            Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId))!.Status);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Already_Allowed_auto_apply_preference_preparation_creates_no_request_or_effect(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var result = await Approvals(f).PreparePreferencesAsync(new(setup.Tenant, setup.Run.AgentRunId), TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(Preferences)), setup.Run.Revision, setup.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.Allowed, result.Decision); Assert.Null(result.Request); await NoEffect(f, setup);
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!.Status);
    }

    [Fact]
    public async Task Saved_persona_payloads_keep_their_original_canonical_identity_and_mixed_payloads_reject()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f, mode: HarnessManagementMode.Assisted);
        var request = (await Approvals(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona, setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        Assert.Equal(ApprovalKind.Review, request.Kind);
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var row = await db.Set<TenantApprovalRecord>().SingleAsync(); var json = JsonNode.Parse(row.PayloadJson)!;
            json["Operation"]!.AsObject().Remove("Preferences"); row.PayloadJson = json.ToJsonString(); await db.SaveChangesAsync();
        }
        Assert.Equal(request, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, request.RequestId));
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var row = await db.Set<TenantApprovalRecord>().SingleAsync(); var json = JsonNode.Parse(row.PayloadJson)!;
            json["Operation"]!["Preferences"] = JsonSerializer.SerializeToNode(new PreparedConversationPreferences(null, "vi", null)); row.PayloadJson = json.ToJsonString(); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, request.RequestId).AsTask());
    }
}
