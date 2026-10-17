using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Approval_request_retains_the_original_persona_for_a_saved_human_review(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f, permissions: false, mode: HarnessManagementMode.Assisted);
        var request = (await Approvals(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId),
            setup.Agent.Instance.Persona with { Name = "A reviewed persona" }, setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var view = await Approvals(f).ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        var json = JsonSerializer.SerializeToElement(view.Request);
        Assert.True(json.TryGetProperty("Snapshot", out var snapshot), "The saved request must retain review context, rather than reconstructing it after an effect.");
        Assert.Equal(setup.Agent.Instance.Persona.Name, snapshot.GetProperty("BeforePersona").GetProperty("Name").GetString());
        var legacy = TenantApprovalRecord.From(request with { Snapshot = null }).Domain();
        Assert.Null(legacy.Snapshot); Assert.Equal(request.Operation, legacy.Operation);
        var corrupt = TenantApprovalRecord.From(request with { Snapshot = request.Snapshot! with { PreferenceDefaults = new("balanced", "en", true) } });
        Assert.Throws<InvalidOperationException>(() => corrupt.Domain());
        Assert.True(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        Assert.Equal(setup.Agent.Instance.Persona.Name, view.Review.Changes.Single(c => c.Field == "name").Before);
        Assert.Equal("A reviewed persona", view.Review.Changes.Single(c => c.Field == "name").After);
        await ChangeManagement(f, setup, setup.Agent.Instance.HarnessManagement!.Policy with { Frozen = true });
        var stale = await Approvals(f).ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        Assert.False(stale.Review!.CanDecide); Assert.True(stale.Review.CanCancel);
        Assert.Equal("operation_witness_changed", stale.Review.DecisionUnavailableReason);
        Assert.Equal(view.Review.Changes, stale.Review.Changes);
        var cancelled = await Approvals(f).CancelAsync(setup.Tenant, request.RequestId, setup.Owner, request.Operation.ActionHash, stale.SecurityRevision, request.Revision);
        var terminal = await Approvals(f).ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        Assert.Equal(TenantApprovalStatus.Cancelled, cancelled.Status); Assert.False(terminal.Review!.CanDecide); Assert.False(terminal.Review.CanCancel);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Saved_preference_review_survives_effect_and_adapter_reopen_without_rewriting_before_values(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ApprovedPreference(f, false);
        var request = setup.Request; var service = Approvals(f);
        var before = await service.ReadViewAsync(request.TenantId, request.RequestId, setup.Environment.Owner);
        Assert.False(before.Review!.CanDecide); Assert.Equal("unrecorded", before.Review.ExecutionState);
        var change = Assert.Single(before.Review.Changes); Assert.Equal("en", change.Before); Assert.Equal("vi", change.After);
        var receipt = await ApprovedEffects(f).ExecuteAsync(new(request.TenantId, setup.Claimed.AgentRunId), request.RequestId,
            request.Operation.OperationId, setup.Claimed.Claim!.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        ITenantApprovalStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : (ITenantApprovalStore)f.Iam;
        var saved = await reopened.FindApprovalAsync(request.TenantId, request.RequestId);
        Assert.Equal(request.Snapshot, saved!.Snapshot); Assert.Equal(request.Operation.ActionHash, saved.Operation.ActionHash);
        var after = await service.ReadViewAsync(request.TenantId, request.RequestId, setup.Environment.Owner);
        Assert.Equal("recorded", after.Review!.ExecutionState); Assert.Equal(change, Assert.Single(after.Review.Changes));
        Assert.NotNull(after.Decision); Assert.Equal(receipt, after.OperationResult);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Review_displays_inherited_defaults_for_exact_clear_and_expiry_disables_actions(bool sqlite)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f, permissions: false, mode: HarnessManagementMode.Assisted);
        var preferences = TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse("""{"set":{"askOneQuestionAtATime":false},"clear":["language"]}"""));
        var r = (await Approvals(f).PreparePreferencesAsync(new(setup.Tenant, setup.Run.AgentRunId), preferences, setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var view = await Approvals(f).ReadViewAsync(setup.Tenant, r.RequestId, setup.Owner);
        var language = view.Review!.Changes.Single(c => c.Field == "language"); Assert.True(language.RestoresDefault);
        Assert.Equal("en", language.Before); Assert.Equal("en", language.After); Assert.True(language.AfterAvailable);
        Assert.Equal(false, view.Review.Changes.Single(c => c.Field == "askOneQuestionAtATime").After);
        clock.Advance(TimeSpan.FromHours(1));
        var expired = await Approvals(f).ReadViewAsync(setup.Tenant, r.RequestId, setup.Owner);
        Assert.False(expired.Review!.CanDecide); Assert.False(expired.Review.CanCancel); Assert.Equal("approval_expired", expired.Review.DecisionUnavailableReason);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stopped_source_does_not_offer_decision_or_cancel_even_if_request_is_still_pending(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f, permissions: false, mode: HarnessManagementMode.Assisted);
        var r = (await Approvals(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Stop review" }, setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var waiting = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(waiting.Owner, waiting.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.RequestCancellation(waiting.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        var view = await Approvals(f).ReadViewAsync(setup.Tenant, r.RequestId, setup.Owner);
        Assert.False(view.Review!.CanDecide); Assert.False(view.Review.CanCancel); Assert.Equal("run_unavailable", view.Review.DecisionUnavailableReason);
    }
}
