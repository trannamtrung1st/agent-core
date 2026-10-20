using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static async Task<BackgroundSetup> CompletionPending(Fixture f, BackgroundSetup parent, Guid child, long revision, string call,
        Guid? token = null, string? usage = null, IReadOnlyList<ModelMessage>? prior = null)
    {
        var tool = token is null ? ToolCatalog.BackgroundTake : ToolCatalog.BackgroundAcknowledge;
        var args = token is null ? JsonSerializer.Serialize(new { backgroundSessionId = child, revision })
            : JsonSerializer.Serialize(new { backgroundSessionId = child, revision, token, usage });
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([..prior ?? [], new(ModelRole.Assistant, "", ToolCalls: [new(call, tool, args)])]), 1, 0, 100000);
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Checkpoint(parent.Parent.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, null), f.Ids.NewId());
        return parent with { Parent = run };
    }
    private static async Task<(Guid Session, AgentRun Run)> FinishedCompletion(Fixture f, BackgroundSetup parent, string call = "start")
    {
        var child = await BackgroundChild(f, parent, call); var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var claimed = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, claimed.Run, claimed.Snapshot);
        var run = await runtime.CommitTenantOutcomeAsync(claimed.Frame, outcome.Snapshot, claimed.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        return (child.Session.SessionId, run);
    }
    private static async Task<CompletionInboxItem> ReadCompletion(Fixture f, BackgroundSetup parent, Guid session)
        => (await ((ITenantRuntimeStore)f.Iam).ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundInspect, session, null, false, 1)).Items[0].Inbox!;

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_inbox_claim_ack_and_answer_are_exclusive_idempotent_and_atomic(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z")), builtInAuthority: true);
        var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent); var child = await FinishedCompletion(f, parent);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var page = await runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundList, null, null, true, 1);
        Assert.Single(page.Items); Assert.Equal(1, page.PendingCount); Assert.False(page.HasMore);
        var item = await ReadCompletion(f, parent, child.Session); parent = await CompletionPending(f, parent, child.Session, item.Revision, "take");
        var taken = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "take", f.Ids.NewId(), null)).Inbox!;
        var repeat = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "take", f.Ids.NewId(), null)).Inbox!;
        Assert.Equal(taken, repeat); Assert.Equal(CompletionInboxStatus.Claimed, taken.Status);
        Assert.True(await runtime.HasTenantCompletionClaimAsync(parent.Frame, false)); Assert.False(await runtime.HasTenantCompletionClaimAsync(parent.Frame, true));
        parent = await CompletionPending(f, parent, child.Session, taken.Revision, "ack", taken.ClaimToken, "Used the evidence");
        var ack = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, taken.Revision, "ack", taken.ClaimToken!.Value, "Used the evidence")).Inbox!;
        Assert.Equal(CompletionInboxStatus.Claimed, ack.Status); Assert.NotNull(ack.Acknowledgment); Assert.Null(ack.HandledByRunId);
        Assert.True(await runtime.HasTenantCompletionClaimAsync(parent.Frame, true));
        var saved = (await runtime.LoadTenantRuntimeAsync(parent.Frame))!; var outcome = BackgroundOutcome(f, parent.Parent, saved);
        await Assert.ThrowsAsync<AgentCoreException>(() => runtime.CommitTenantOutcomeAsync(parent.Frame, outcome.Snapshot, saved.Revision - 1, outcome.Complete, null, f.Ids.NewId()).AsTask());
        Assert.Equal(CompletionInboxStatus.Claimed, (await ReadCompletion(f, parent, child.Session)).Status);
        await runtime.CommitTenantOutcomeAsync(parent.Frame, outcome.Snapshot, saved.Revision, outcome.Complete, null, f.Ids.NewId());
        var source = (await runtime.ReadTenantBackgroundCompletionAsync(parent.Parent.Owner, child.Run.AgentRunId))!;
        Assert.Equal(CompletionInboxStatus.Handled, source.Item!.Status); Assert.Equal(parent.Parent.AgentRunId, source.Item.HandledByRunId);
        Assert.Null(source.Item.ClaimToken); Assert.Empty(await runtime.ListTenantBackgroundCompletionsAsync(100));
    }

    [Theory] [InlineData(false, "failure")] [InlineData(true, "failure")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "retry")] [InlineData(true, "retry")]
    [InlineData(false, "expiry")] [InlineData(true, "expiry")]
    public async Task Tenant_completion_inbox_unfinished_parent_releases_acknowledgment_and_token(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent);
        var child = await FinishedCompletion(f, parent); var runtime = (ITenantRuntimeStore)f.Iam; var item = await ReadCompletion(f, parent, child.Session);
        parent = await CompletionPending(f, parent, child.Session, item.Revision, "take");
        item = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "take", f.Ids.NewId(), null)).Inbox!;
        parent = await CompletionPending(f, parent, child.Session, item.Revision, "ack", item.ClaimToken, "Provisional usage");
        item = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "ack", item.ClaimToken!.Value, "Provisional usage")).Inbox!;
        AgentRunCommand command;
        if (mode == "expiry") { clock.Advance(TimeSpan.FromMinutes(6)); command = new AgentRunCommand.Recover(parent.Parent.Revision, clock.GetUtcNow()); }
        else if (mode == "cancel")
        {
            var requested = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, new AgentRunCommand.RequestCancellation(parent.Parent.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
            command = new AgentRunCommand.CommitCancellation(requested.Revision, clock.GetUtcNow(), parent.Frame.Generation, null);
        }
        else command = new AgentRunCommand.Fail(parent.Parent.Revision, clock.GetUtcNow(), parent.Frame.Generation, "test-failure", "Test interruption", mode == "retry", mode == "retry" ? clock.GetUtcNow().AddSeconds(1) : null);
        await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, command, f.Ids.NewId());
        var source = (await runtime.ReadTenantBackgroundCompletionAsync(parent.Parent.Owner, child.Run.AgentRunId))!;
        Assert.Equal(CompletionInboxStatus.Pending, source.Item!.Status); Assert.Null(source.Item.Acknowledgment); Assert.Null(source.Item.ClaimToken); Assert.Null(source.Item.HandledByRunId);
    }

    [Theory] [InlineData(false, "token")] [InlineData(true, "token")]
    [InlineData(false, "generation")] [InlineData(true, "generation")]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "policy")] [InlineData(true, "policy")]
    [InlineData(false, "call")] [InlineData(true, "call")]
    public async Task Tenant_completion_inbox_rechecks_generation_exact_call_token_and_current_source_authority(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z")), builtInAuthority: true);
        var parent = await BackgroundParent(f); var read = await WaitReadAuthority(f, parent); var child = await FinishedCompletion(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        var item = await ReadCompletion(f, parent, child.Session); parent = await CompletionPending(f, parent, child.Session, item.Revision, "take");
        item = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "take", f.Ids.NewId(), null)).Inbox!;
        var token = boundary == "token" ? f.Ids.NewId() : item.ClaimToken!.Value;
        parent = await CompletionPending(f, parent, child.Session, item.Revision, "ack", token, "Use evidence");
        if (boundary == "read") await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, read, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        if (boundary == "policy") await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        var frame = boundary == "generation" ? parent.Frame with { Generation = f.Ids.NewId() } : parent.Frame;
        var refusal = await Record.ExceptionAsync(() => runtime.MutateTenantCompletionAsync(frame, child.Session, item.Revision, boundary == "call" ? "invented" : "ack", token, "Use evidence").AsTask());
        Assert.True(refusal is AgentCoreException or AccessDeniedException, refusal?.ToString() ?? "Expected a refusal.");
        var source = (await runtime.ReadTenantBackgroundCompletionAsync(parent.Parent.Owner, child.Run.AgentRunId))!;
        Assert.Equal(CompletionInboxStatus.Claimed, source.Item!.Status); Assert.Null(source.Item.Acknowledgment);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_inbox_filters_before_page_count_and_current_context_reuse(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z")), builtInAuthority: true);
        var parent = await BackgroundParent(f); var assignment = await WaitReadAuthority(f, parent); var child = await FinishedCompletion(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        var text = JsonSerializer.Serialize(new { backgroundSessionId = child.Session, sourceAgentRunId = child.Run.AgentRunId });
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, text, ToolCallId: "inspect", Name: ToolCatalog.BackgroundInspect)]), 1, 0, 100000);
        var run = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Checkpoint(parent.Parent.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, null), f.Ids.NewId());
        var guard = new TenantWaitSourceAuthority(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Authorizer);
        await guard.RequireStoredAsync(run, default);
        await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, assignment, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        var page = await runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundList, null, null, false, 1);
        Assert.Empty(page.Items); Assert.Equal(0, page.PendingCount); Assert.False(page.HasMore);
        await Assert.ThrowsAsync<AgentCoreException>(() => runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundInspect, child.Session, null, false, 1).AsTask());
        Assert.Equal("completion_source_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => guard.RequireStoredAsync(run, default).AsTask())).ReasonCode);
        var saved = (await runtime.LoadTenantRuntimeAsync(parent.Frame))!; var outcome = BackgroundOutcome(f, run, saved);
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.CommitTenantOutcomeAsync(parent.Frame, outcome.Snapshot, saved.Revision, outcome.Complete, null, f.Ids.NewId()).AsTask());
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, run.AgentRunId))!.Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_inbox_metadata_is_independently_filtered_and_rechecked_without_content_reads(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, artifactAuthority: true);
        var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        var child = await BackgroundChild(f, parent); await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var active = await ClaimBackground(f, child.Run); var artifacts = (ITenantArtifactStore)f.Artifacts; var files = new List<ArtifactRecord>();
        for (var i = 0; i < 4; i++)
        {
            files.Add(await artifacts.CreateTenantAsync(active.Frame, f.Ids.NewId(), "evidence-" + i + ".txt", "text/plain", "private bytes"u8.ToArray()));
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        var outcome = BackgroundOutcome(f, active.Run, active.Snapshot);
        var final = await runtime.CommitTenantOutcomeAsync(active.Frame, outcome.Snapshot, active.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        async Task<TenantCompletionView> Inspect() => (await runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundInspect, child.Session.SessionId, null, false, 1)).Items[0];
        Assert.Empty((await Inspect()).Artifacts);
        var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, null, "Exact metadata", ["artifacts.read"], (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        foreach (var file in files.Take(2))
            await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, parent.Setup.Agent.PrincipalId, role.RoleId,
                TenantArtifactRules.Scope(parent.Setup.Tenant, file.ArtifactId), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        var view = await Inspect(); Assert.Equal(files.Take(2).Select(a => a.ArtifactId).Order(), view.Artifacts.Select(a => a.ArtifactId).Order());
        var text = JsonSerializer.Serialize(new { backgroundSessionId = child.Session.SessionId, sourceAgentRunId = final.AgentRunId,
            artifacts = view.Artifacts.Select(a => new { artifactId = a.ArtifactId }) });
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, text, ToolCallId: "inspect", Name: ToolCatalog.BackgroundInspect)]), 1, 0, 100000);
        var run = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Checkpoint(parent.Parent.Revision, clock.GetUtcNow(), parent.Frame.Generation, checkpoint, null), f.Ids.NewId());
        var guard = new TenantWaitSourceAuthority(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Authorizer, artifacts);
        await guard.RequireStoredAsync(run, default);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, clock)
            .SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "artifacts.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision, 0);
        Assert.Empty((await Inspect()).Artifacts);
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.RequireStoredAsync(run, default).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_inbox_unexpired_claim_renews_and_transfers_only_on_same_Run_signal_wake(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); await WaitReadAuthority(f, parent);
        var child = await FinishedCompletion(f, parent); var runtime = (ITenantRuntimeStore)f.Iam; var item = await ReadCompletion(f, parent, child.Session);
        parent = await CompletionPending(f, parent, child.Session, item.Revision, "take");
        item = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "take", f.Ids.NewId(), null)).Inbox!;
        clock.Advance(TimeSpan.FromSeconds(10));
        var renewed = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Renew(parent.Parent.Revision, clock.GetUtcNow(), parent.Frame.Generation, clock.GetUtcNow().AddMinutes(8)), f.Ids.NewId());
        parent = parent with { Parent = renewed }; var after = await ReadCompletion(f, parent, child.Session);
        Assert.Equal(item.ClaimToken, after.ClaimToken); Assert.Equal(renewed.Claim!.LeaseExpiresAtUtc, after.ClaimExpiresAtUtc);
        var pending = await WaitCheckpoint(f, parent, null, 1);
        var waiting = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId, pending.Command, f.Ids.NewId());
        clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = await runtime.ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId,
            new AgentRunCommand.ResumeWait(waiting.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var old = parent.Frame; parent = parent with { Parent = resumed, Frame = old with { Generation = resumed.Claim!.Generation } };
        after = await ReadCompletion(f, parent, child.Session);
        Assert.Equal(item.ClaimToken, after.ClaimToken); Assert.Equal(resumed.Claim!.Generation, after.ClaimGeneration);
        Assert.True(await runtime.HasTenantCompletionClaimAsync(parent.Frame, false));
        await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.HasTenantCompletionClaimAsync(old, false).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_inbox_acknowledgment_rechecks_source_authority_in_atomic_final_save(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z")), builtInAuthority: true);
        var parent = await BackgroundParent(f); var reader = await WaitReadAuthority(f, parent); var child = await FinishedCompletion(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        var item = await ReadCompletion(f, parent, child.Session); parent = await CompletionPending(f, parent, child.Session, item.Revision, "take");
        item = (await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "take", f.Ids.NewId(), null)).Inbox!;
        parent = await CompletionPending(f, parent, child.Session, item.Revision, "ack", item.ClaimToken, "Use child result");
        await runtime.MutateTenantCompletionAsync(parent.Frame, child.Session, item.Revision, "ack", item.ClaimToken!.Value, "Use child result");
        await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, reader, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        var saved = (await runtime.LoadTenantRuntimeAsync(parent.Frame))!; var outcome = BackgroundOutcome(f, parent.Parent, saved);
        Assert.Equal("completion_source_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.CommitTenantOutcomeAsync(parent.Frame, outcome.Snapshot, saved.Revision, outcome.Complete, null, f.Ids.NewId()).AsTask())).ReasonCode);
        Assert.DoesNotContain((await runtime.LoadTenantRuntimeAsync(parent.Frame))!.Entries, e => e.EntryId == outcome.Complete.OutcomeEntryId);
        var source = (await runtime.ReadTenantBackgroundCompletionAsync(parent.Parent.Owner, child.Run.AgentRunId))!;
        Assert.Equal(CompletionInboxStatus.Claimed, source.Item!.Status); Assert.Null(source.Item.HandledByRunId);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_inbox_applies_resource_read_filter_before_cursor_page_and_totals(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T04:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var parent = await BackgroundParent(f); var runtime = (ITenantRuntimeStore)f.Iam;
        var first = await FinishedCompletion(f, parent, "first"); clock.Advance(TimeSpan.FromSeconds(1));
        var second = await FinishedCompletion(f, parent, "second"); var denied = second; var tenant = parent.Setup.Tenant;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var sessions = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, tenant, null, "Session evidence reader", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, sessions.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, tenant, null, "Exact Run reader", ["agent-runs.read"], await Revision());
        Guid deniedAssignment = Guid.Empty;
        foreach (var id in new[] { first.Run.AgentRunId, second.Run.AgentRunId })
        {
            var assignment = await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, role.RoleId, TenantRunRules.Scope(tenant, id), await Revision());
            if (id == second.Run.AgentRunId) deniedAssignment = assignment.AssignmentId;
        }
        var page = await runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundList, null, null, true, 1);
        Assert.Equal(second.Run.AgentRunId, Assert.Single(page.Items).Source.Run.AgentRunId); Assert.Equal(2, page.PendingCount); Assert.True(page.HasMore);
        var next = await runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundList, null, page.NextCursor, true, 1);
        Assert.Equal(first.Run.AgentRunId, Assert.Single(next.Items).Source.Run.AgentRunId); Assert.False(next.HasMore); Assert.Equal(2, next.PendingCount);
        await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, tenant, deniedAssignment, await Revision());
        var filtered = await runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundList, null, null, true, 1);
        Assert.Equal(first.Run.AgentRunId, Assert.Single(filtered.Items).Source.Run.AgentRunId); Assert.Equal(1, filtered.PendingCount); Assert.False(filtered.HasMore);
        await Assert.ThrowsAsync<AgentCoreException>(() => runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundList, null, denied.Run.AgentRunId, false, 1).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => runtime.ReadTenantCompletionsAsync(parent.Frame, ToolCatalog.BackgroundInspect, denied.Session, null, false, 1).AsTask());
    }

}
