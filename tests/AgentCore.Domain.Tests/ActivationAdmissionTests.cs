using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class ActivationAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_batched_turn_freezes_ordered_sources_and_one_response_identity()
    {
        var sources = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var original = sources.ToArray();
        var activation = UserActivation(Guid.NewGuid(), sources);
        sources[0] = Guid.NewGuid();
        var run = NewRun(activation);
        Assert.Equal(original, activation.SourceEntryIds);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)activation.SourceEntryIds)[0] = Guid.NewGuid());
        Assert.Equal(activation.ActivationId, run.ActivationId);
        Assert.Equal(activation.SessionId, run.SessionId);
        Assert.NotNull(run.ResponseId);
        var reordered = UserActivation(activation.SessionId, original.Reverse().ToArray());
        Assert.NotEqual(activation.SourceFingerprint, reordered.SourceFingerprint);
        var replay = UserActivation(activation.SessionId, original);
        Assert.Equal(activation.SourceFingerprint, replay.SourceFingerprint);
    }

    [Fact]
    public void Retry_preserves_admission_and_response_but_continuation_is_a_new_turn()
    {
        var initial = NewRun(UserActivation(Guid.NewGuid(), [Guid.NewGuid()]));
        var running = initial.TakeClaim(Guid.NewGuid(), Now, Now.AddMinutes(1));
        var retry = running.Fail(running.Revision, running.Claim!.Generation, "model-unavailable", "Unavailable",
            true, Now.AddSeconds(1), Now.AddSeconds(2));
        var resumed = retry.TakeClaim(Guid.NewGuid(), Now.AddSeconds(2), Now.AddMinutes(2));
        Assert.Equal(initial.AgentRunId, resumed.AgentRunId);
        Assert.Same(initial.Admission.Activation, resumed.Admission.Activation);
        Assert.Equal(initial.ResponseId, resumed.ResponseId);
        Assert.Equal(2, resumed.AttemptCount);
        var continued = NewRun(UserActivation(initial.SessionId, [Guid.NewGuid()]));
        Assert.Equal(initial.SessionId, continued.SessionId);
        Assert.NotEqual(initial.ActivationId, continued.ActivationId);
        Assert.NotEqual(initial.AgentRunId, continued.AgentRunId);
        Assert.NotEqual(initial.ResponseId, continued.ResponseId);
    }

    [Fact]
    public void Invalid_or_empty_sources_and_forged_background_provenance_are_rejected()
    {
        var sessionId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => UserActivation(sessionId, []));
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => UserActivation(sessionId, [id, id]));
        Assert.Throws<ArgumentException>(() => UserActivation(sessionId, [Guid.Empty]));
        Assert.Throws<ArgumentException>(() => UserActivation(sessionId,
            Enumerable.Range(0, Activation.MaxSourceEntries + 1).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.Throws<ArgumentException>(() => new Activation(Guid.NewGuid(), sessionId,
            ActivationKind.ImmediateBackground, [], null, null, sessionId, Guid.NewGuid(), "start", Now));
        Assert.Throws<ArgumentException>(() => new Activation(Guid.NewGuid(), sessionId,
            ActivationKind.ScheduledWork, [], null, null, null, null, "scheduled", Now));
    }

    [Fact]
    public void Background_origin_remains_immutable_when_chat_visibility_is_added()
    {
        var initialRun = Guid.NewGuid();
        var origin = new SessionOrigin(SessionOriginKind.ImmediateBackground,
            Guid.NewGuid(), Guid.NewGuid(), initialRun, reportCompletionToOrigin: true);
        Assert.Equal(SessionSurface.BackgroundWork, origin.InitialSurface);
        var foregrounded = SessionOrigin.ContinueInChat(origin.InitialSurface);
        Assert.Equal(SessionSurface.ChatList | SessionSurface.BackgroundWork, foregrounded);
        Assert.Equal(foregrounded, SessionOrigin.ContinueInChat(foregrounded));
        Assert.Equal(SessionOriginKind.ImmediateBackground, origin.Kind);
        Assert.True(origin.MayReportCompletion(initialRun));
        Assert.False(origin.MayReportCompletion(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new SessionOrigin(SessionOriginKind.AutomationOccurrence,
            initialBackgroundAgentRunId: initialRun, automationId: Guid.NewGuid(), triggerOccurrenceId: Guid.NewGuid(),
            reportCompletionToOrigin: true));
        Assert.Throws<ArgumentException>(() => new SessionOrigin(SessionOriginKind.ImmediateBackground,
            Guid.NewGuid(), Guid.NewGuid(), initialRun, automationId: Guid.NewGuid()));
    }

    [Fact]
    public void Quiet_completion_does_not_invent_a_chat_entry_and_response_requires_one()
    {
        var run = NewRun(UserActivation(Guid.NewGuid(), [Guid.NewGuid()]))
            .TakeClaim(Guid.NewGuid(), Now, Now.AddMinutes(1));
        var quiet = run.Complete(run.Revision, run.Claim!.Generation, "", Now.AddSeconds(1),
            outcomeKind: AgentRunOutcomeKind.NoAction);
        Assert.True(quiet.IsTerminal);
        Assert.Equal(AgentRunOutcomeKind.NoAction, quiet.Result!.OutcomeKind);
        Assert.Empty(quiet.Result.Text);
        Assert.False(quiet.Result.AttentionRequired);
        Assert.Null(quiet.OutcomeEntryId);
        Assert.Throws<ArgumentException>(() => run.Complete(run.Revision, run.Claim.Generation, "Result", Now.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => run.Complete(run.Revision, run.Claim.Generation, "", Now.AddSeconds(1),
            outcomeKind: AgentRunOutcomeKind.NoAction, outcomeEntryId: Guid.NewGuid()));
        var attention = run.Complete(run.Revision, run.Claim.Generation, "Check this", Now.AddSeconds(1),
            outcomeKind: AgentRunOutcomeKind.NeedsAttention);
        Assert.True(attention.Result!.AttentionRequired);
    }

    [Fact]
    public void Projection_is_frozen_bounded_and_survives_claim_recovery()
    {
        var capabilities = new[] { "browser" };
        var catalog = new[] { new EffectiveSkill("definition:review", SkillOrigin.Definition, "review",
            "Review", "Review safely", "Read the page", SkillProjection.OnDemand, capabilities, []) };
        var baseRun = NewRun(UserActivation(Guid.NewGuid(), [Guid.NewGuid()]), catalog);
        capabilities[0] = "email";
        Assert.Equal("browser", baseRun.PinnedSkillCatalog[0].RequiredCapabilities[0]);
        var run = baseRun.TakeClaim(Guid.NewGuid(), Now, Now.AddMinutes(1));
        run = run.AdmitActiveSkills(run.Revision, run.Claim!.Generation, ["definition:review"], Now.AddSeconds(1));
        var names = new[] { "browser.observe" };
        run = run.AdmitCapabilities(run.Revision, run.Claim!.Generation, names, Now.AddSeconds(2));
        names[0] = "email.send";
        var recovered = run.RecoverExpiredClaim(Now.AddMinutes(1));
        var resumed = recovered.TakeClaim(Guid.NewGuid(), Now.AddMinutes(1), Now.AddMinutes(2));
        Assert.Equal(["definition:review"], resumed.ActiveSkillKeys);
        Assert.Equal(["browser.observe"], resumed.LoadedCapabilityIds);
        Assert.Equal(1, resumed.SkillLoadCount);
        Assert.Equal(1, resumed.CapabilityLoadCount);
        Assert.Throws<ArgumentException>(() => resumed.AdmitActiveSkills(resumed.Revision, resumed.Claim!.Generation,
            ["definition:unpublished"], Now.AddMinutes(1)));
    }

    private static Activation UserActivation(Guid sessionId, IReadOnlyList<Guid> sources) =>
        new(Guid.NewGuid(), sessionId, ActivationKind.UserTurn, sources, null, null, null, null, "user:batch", Now);

    [Fact]
    public void Idempotent_effect_acknowledgements_still_reject_stale_generation()
    {
        var run = NewRun(UserActivation(Guid.NewGuid(), [Guid.NewGuid()]))
            .TakeClaim(Guid.NewGuid(), Now, Now.AddMinutes(1));
        var other = Guid.NewGuid();
        Assert.Throws<AgentRunTransitionException>(() => run.ClearSideEffect(run.Revision, other, Now));
        Assert.Throws<AgentRunTransitionException>(() => run.AcceptBrowserObservation(run.Revision, other, Now));
        run = run.MarkSideEffect(run.Revision, run.Claim!.Generation, AgentRunSideEffectDisposition.Prepared,
            "tool-1", new string('a', 64), Now);
        var failure = Assert.Throws<AgentRunTransitionException>(() => run.MarkSideEffect(run.Revision, other,
            AgentRunSideEffectDisposition.Prepared, "tool-1", new string('a', 64), Now));
        Assert.Equal(AgentRunTransitionFailure.StaleGeneration, failure.Failure);
    }

    private static AgentRun NewRun(Activation activation, IReadOnlyList<EffectiveSkill>? catalog = null) =>
        AgentRun.Create(Guid.NewGuid(), new AgentRunOwner(Guid.NewGuid(), Guid.NewGuid()),
            new AgentRunAdmission(activation, "general-assistant", 16,
                new AgentIdentity("Alex", "Assistant", "Help", "Calm"), Guid.NewGuid()),
            new AgentRunModelPin("synthetic", "synthetic", "synthetic", null), 3, Now, catalog);
}
