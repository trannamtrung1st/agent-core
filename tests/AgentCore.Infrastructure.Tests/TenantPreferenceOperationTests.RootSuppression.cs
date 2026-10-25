using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Security;
using System.Text.Json.Nodes;
using System.Reflection;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    [Theory]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "expire")] [InlineData(true, "expire")]
    [InlineData(false, "pending")] [InlineData(true, "pending")]
    public async Task Refused_same_root_preference_does_not_create_a_new_request_when_target_revisions_change(bool sqlite, string outcome)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f, permissions: false);
        var service = Approvals(f); var parameters = TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(Preferences));
        var request = (await service.PreparePreferencesAsync(new(setup.Tenant, setup.Run.AgentRunId), parameters, setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        if (outcome == "reject") await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false, request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        if (outcome == "cancel") await service.CancelAsync(setup.Tenant, request.RequestId, setup.Owner, request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        if (outcome == "expire") clock.Advance(TimeSpan.FromHours(1));
        await service.DispatchPendingAsync();
        var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        await ChangeManagement(f, setup, setup.Agent.Instance.HarnessManagement!.Policy);
        var retry = await service.PreparePreferencesAsync(new(setup.Tenant, current.AgentRunId), parameters, current.Revision, current.Claim?.Generation);
        Assert.Equal(outcome == "pending" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied, retry.Decision);
        Assert.Equal(outcome switch { "reject" => "previously_rejected", "cancel" => "previously_cancelled", "expire" => "previously_expired", _ => "already_pending" }, retry.ReasonCode);
        Assert.Equal(request.RequestId, retry.Request!.RequestId);
        Assert.Single((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Items);
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "approvals.request");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Refused_same_root_preference_cannot_bypass_review_after_executor_gains_permission(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f, permissions: false);
        var service = Approvals(f); var store = (ITenantRunStore)f.Iam;
        var request = (await service.PreparePreferencesAsync(new(setup.Tenant, setup.Run.AgentRunId),
            TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(Preferences)), setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false, request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        await service.DispatchPendingAsync();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Now allowed", ["agents.read", "agents.update"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, role.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var queued = (await store.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        var current = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var result = await Service(f).ExecuteAsync(new(setup.Tenant, current.AgentRunId), current.Claim!.Generation, "bypass", Preferences);
        Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Equal("previously_rejected", result.ReasonCode); Assert.Null(result.Receipt);
        var patch = TenantPreferenceToolContract.Parse(Preferences);
        var commit = new TenantPreferenceCommit(f.Ids.NewId(), current.AgentRunId, current.Claim.Generation, current.Revision,
            "bypass-store", patch.CanonicalArguments, patch.ParametersHash, setup.Agent.Instance.Revision,
            TenantPreferenceToolContract.ActionHash(current, setup.Agent.Instance.Revision, patch.ParametersHash));
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantPreferenceOperationStore)f.Iam).TryCommitPreferencesAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), setup.Tenant, setup.Agent.PrincipalId, "operations.preferences.apply", current.AgentInstanceId.ToString("D"), "allowed", "operation_completed", at)).AsTask());
        Assert.Equal("previously_rejected", denied.ReasonCode);
        Assert.Null(await ((ITenantPreferenceOperationStore)f.Iam).FindDirectOperationReceiptAsync(setup.Tenant, current.AgentRunId, "bypass-store"));
        Assert.Equal(state.Tenant.Revision, await Revision());
        Assert.Equal(setup.Agent.Instance.Revision, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, current.AgentInstanceId))!.Instance.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Approval_operation_storm_limit_is_atomic_keeps_pending_charged_and_releases_terminal_requests_after_window(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f, permissions: false);
        var service = Approvals(f); var requests = new List<TenantApprovalRequest>();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var parameters = TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(Preferences));
        AgentRun current = setup.Run;
        for (var i = 0; i <= TenantApprovalLimits.Operation; i++)
        {
            if (i > 0)
            {
                var admission = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Run.SessionId, setup.Owner, f.Ids.NewId(), "Independent request", await Revision(), i + 1);
                current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, admission.RunId))!;
            }
            var before = await Revision();
            var result = await service.PreparePreferencesAsync(new(setup.Tenant, current.AgentRunId), parameters, current.Revision, current.Claim?.Generation);
            if (i < TenantApprovalLimits.Operation) { requests.Add(result.Request!); Assert.Equal("approval_pending", result.ReasonCode); }
            else { Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Equal("approval_rate_limited", result.ReasonCode); Assert.Null(result.Request); Assert.Equal(before, await Revision()); }
        }
        Assert.Equal(TenantApprovalLimits.Operation, (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Count(a => a.Action == "approvals.request"));
        var throttle = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "authorization.operation" && a.ReasonCode == "approval_rate_limited");
        Assert.Equal("denied", throttle.Decision); Assert.Equal(current.AgentRunId, throttle.Execution!.Context.RunId);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("approval_rate_limited", (await service.PreparePreferencesAsync(new(setup.Tenant, current.AgentRunId), parameters, current.Revision)).ReasonCode);
        var duplicate = await service.PreparePreferencesAsync(new(setup.Tenant, requests[0].RunId), parameters, 1);
        Assert.Equal("already_pending", duplicate.ReasonCode); Assert.Equal(requests[0].RequestId, duplicate.Request!.RequestId);
        foreach (var request in requests) await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false, request.Operation.ActionHash, await Revision(), 1);
        // All requests are already older than the rolling window once they become terminal.
        var allowed = await service.PreparePreferencesAsync(new(setup.Tenant, current.AgentRunId), parameters, current.Revision);
        Assert.Equal("approval_pending", allowed.ReasonCode); Assert.NotNull(allowed.Request);
        Assert.Equal(AgentRunStatus.WaitingForApproval, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, current.AgentRunId))!.Status);
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Approval_root_ceiling_survives_rolling_windows_but_duplicate_refusal_does_not_spend_another_slot(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f, permissions: false);
        var service = Approvals(f); var runs = (ITenantRunStore)f.Iam; AgentRun current = setup.Run;
        var persona = setup.Agent.Instance.Persona; TenantApprovalRequest? first = null;
        for (var i = 0; i < TenantApprovalLimits.Root; i++)
        {
            var request = (await service.PreparePersonaAsync(new(setup.Tenant, current.AgentRunId), persona with { Name = "Root loop " + i }, current.Revision, current.Claim?.Generation)).Request!;
            Assert.NotNull(request); first ??= request;
            await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false, request.Operation.ActionHash,
                (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
            await service.DispatchPendingAsync();
            current = (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var limited = await service.PreparePersonaAsync(new(setup.Tenant, current.AgentRunId), persona with { Name = "Root loop overflow" }, current.Revision);
        Assert.Equal(OperationDecisionKind.Denied, limited.Decision); Assert.Equal("approval_root_limit", limited.ReasonCode); Assert.Null(limited.Request);
        var duplicate = await service.PreparePersonaAsync(new(setup.Tenant, current.AgentRunId), first!.Operation.Persona!, current.Revision);
        Assert.Equal("previously_rejected", duplicate.ReasonCode); Assert.Equal(first.RequestId, duplicate.Request!.RequestId);
        var ceiling = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "authorization.operation" && a.ReasonCode == "approval_root_limit");
        Assert.Equal("denied", ceiling.Decision); Assert.Equal(current.AgentRunId, ceiling.Execution!.Context.RunId);

        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var unchanged = (await runs.FindTenantRunAsync(setup.Tenant, current.AgentRunId))!;
        Assert.Equal(current.Revision, unchanged.Revision); Assert.Equal(current.Status, unchanged.Status); Assert.Equal(current.ApprovalRequestId, unchanged.ApprovalRequestId);
        Assert.Equal(TenantApprovalLimits.Root, (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Count(a => a.Action == "approvals.request"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Semantic_root_lookup_preserves_unicode_escaping_and_material_arguments_while_exact_hash_stays_revision_bound(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f, permissions: false);
        var persona = setup.Agent.Instance.Persona with { Name = "Tiếng Việt \"quoted\" <tag>" };
        var request = (await Approvals(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var changedWitness = TenantApprovalRules.Prepare(f.Ids.NewId(), request.Operation.InstanceId, persona, 99, 98);
        Assert.NotEqual(request.Operation.ActionHash, changedWitness.ActionHash); Assert.Equal(request.Fingerprint, changedWitness.OperationFingerprint);
        var store = (ITenantApprovalStore)f.Iam;
        Assert.Equal(request.RequestId, (await store.FindRootApprovalAsync(setup.Tenant, request.RootActivationId, request.ExecutorPrincipalId, changedWitness))!.RequestId);
        var materialChange = TenantApprovalRules.Prepare(f.Ids.NewId(), request.Operation.InstanceId, persona with { Tone = "Different tone" }, 99, 98);
        Assert.Null(await store.FindRootApprovalAsync(setup.Tenant, request.RootActivationId, request.ExecutorPrincipalId, materialChange));
        Assert.Null(await store.FindRootApprovalAsync(setup.Tenant, f.Ids.NewId(), request.ExecutorPrincipalId, changedWitness));
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Legacy_duplicate_approval_cannot_override_an_earlier_same_root_refusal_and_keeps_exact_grant_witnesses(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ApprovedPreference(f, false);
        var request = setup.Request; var run = setup.Claimed;
        var priorOperation = TenantApprovalRules.PreparePreferences(f.Ids.NewId(), request.Operation.InstanceId, request.Operation.Preferences!, 1, 1);
        var prior = request with { RequestId = f.Ids.NewId(), Operation = priorOperation, Status = TenantApprovalStatus.Rejected,
            Revision = 2, CreatedAt = request.CreatedAt.AddMilliseconds(-1) };
        // Simulate retained history from the old exact-hash root index, including its old serialized getter.
        if (f.Contexts is { } contexts)
        {
            await using var db = await contexts.CreateDbContextAsync(); var row = TenantApprovalRecord.From(prior);
            var payload = JsonNode.Parse(row.PayloadJson)!; payload["Fingerprint"] = prior.Operation.ActionHash; row.PayloadJson = payload.ToJsonString();
            db.Add(row); await db.SaveChangesAsync();
        }
        else ((Dictionary<Guid, TenantApprovalRequest>)typeof(InMemoryIamStore).GetField("_approvals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Iam)!).Add(prior.RequestId, prior);
        var store = (ITenantApprovalStore)f.Iam;
        Assert.Equal(prior.RequestId, (await store.FindRootApprovalAsync(request.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Operation))!.RequestId);
        var receipt = await ApprovedEffects(f).ExecuteAsync(new(request.TenantId, run.AgentRunId), request.RequestId,
            request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal("root_operation_conflict", receipt.ReasonCode);
        var grant = (await store.FindOperationGrantAsync(request.TenantId, request.RequestId))!;
        Assert.Equal(OperationGrantStatus.Revoked, grant.Status); Assert.Equal(request.Operation.ActionHash, grant.ActionHash);
        var target = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, run.AgentInstanceId))!;
        Assert.Equal(request.Operation.ExpectedInstanceRevision, target.Instance.Revision); Assert.Null(target.Instance.SettingsOverrides?.Language);
    }

}
