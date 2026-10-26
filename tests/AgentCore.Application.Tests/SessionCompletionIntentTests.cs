using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class SessionCompletionIntentTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reason\":\" \"}")]
    [InlineData("{\"reason\":\"Done\",\"sessionId\":\"forged\"}")]
    [InlineData("{\"reason\":4}")]
    public void Forged_or_invalid_arguments_never_accept_intent(string arguments)
    {
        using var json = JsonDocument.Parse(arguments);
        Assert.DoesNotContain("accepted", SessionCompletionIntent.Accept(json.RootElement));
    }

    [Fact]
    public void Eligibility_is_bound_to_current_claim_foreground_chat_and_explicit_policy()
    {
        var (run, chat, now) = Fixture();
        Assert.True(SessionCompletionIntent.Eligible(run, chat, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat, now.AddMinutes(5)));
        Assert.False(SessionCompletionIntent.Eligible(run.RequestCancellation(run.Revision, null, now), chat, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat with { SessionId = Guid.NewGuid() }, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat with { AgentInstanceId = Guid.NewGuid() }, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat with { ArchivedAt = now }, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat with { CompletionPolicy = SessionCompletionPolicy.Default }, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat with { CompletionPolicy = new(AgentCompletionAuthority.Advisory, true, true) }, now));
        Assert.False(SessionCompletionIntent.Eligible(run, chat with { RetentionKind = SessionRetentionKind.Temporary, RetentionDueAtUtc = now }, now));
        var (background, _, _) = Fixture(ActivationKind.ManualBackground);
        Assert.False(SessionCompletionIntent.Eligible(background, chat with { SessionId = background.SessionId, AgentInstanceId = background.AgentInstanceId }, now));
    }

    [Fact]
    public void Checkpoint_recovery_preserves_intent_and_requires_durable_current_final_reply()
    {
        var (run, chat, now) = Fixture();
        using var json = JsonDocument.Parse("{\"reason\":\"Requested task finished.\"}");
        ModelMessage[] messages = [new(ModelRole.Tool, SessionCompletionIntent.Accept(json.RootElement), Name: ToolCatalog.SessionComplete, ToolCallId: "complete-1")];
        run = run.SaveCheckpoint(run.Revision, run.Claim!.Generation, new(AgentRunToolCallCheckpoint.Write(messages), 1, 0, 1000), null, now);
        Assert.Equal("Requested task finished.", SessionCompletionIntent.Reason(run));
        var closed = LifecycleTransition.Apply(chat, SessionLifecycleStatus.Completed, LifecycleTransitionSource.Agent, now, SessionCompletionIntent.Reason(run));
        Assert.Throws<AccessDeniedException>(() => SessionCompletionIntent.ValidateOutcome(run, chat, closed, now));
        var final = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.Assistant, "Done.", run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 0, now);
        SessionCompletionIntent.ValidateOutcome(run, chat, closed with { Entries = [final] }, now);
        Assert.Throws<AccessDeniedException>(() => SessionCompletionIntent.ValidateOutcome(run, chat, closed with { Entries = [final with { ResponseId = Guid.NewGuid() }] }, now));
        Assert.Throws<AccessDeniedException>(() => SessionCompletionIntent.ValidateOutcome(run, chat, closed with { Entries = [final], PendingAgentInputIds = [Guid.NewGuid()] }, now));
    }

    private static (AgentRun, SessionSnapshot, DateTimeOffset) Fixture(ActivationKind kind = ActivationKind.UserTurn)
    {
        var now = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero); var session = Guid.NewGuid(); var instance = Guid.NewGuid(); var tenant = Guid.NewGuid(); var principal = Guid.NewGuid(); var runId = Guid.NewGuid();
        var activation = Activation.Start(Guid.NewGuid(), session, kind, [Guid.NewGuid()], null, null, null, null, "completion:test", now);
        var run = AgentRun.Create(runId, new(instance, null, tenant, principal),
            new(activation, "assistant", 1, new AgentIdentity("Alex", "Assistant", "Help", "Calm"), Guid.NewGuid(), AgentRunOutputContract.ConversationResponse, executionContext: new(tenant, principal, Guid.NewGuid(), session, runId, activation.ActivationId, activation.RootActivationId, null, null, kind == ActivationKind.UserTurn ? ExecutionOrigin.UserMessage : ExecutionOrigin.ManualInvocation)),
            new("synthetic", "synthetic", "synthetic", null), 3, now).TakeClaim(Guid.NewGuid(), now, now.AddMinutes(5));
        var chat = new SessionSnapshot(1, session, 1, SampleDefinitions.Examiner, SessionMode.Text, null, SessionStatus.Attached, [], "", 0, null, null, now, now, instance,
            CompletionPolicy: new(AgentCompletionAuthority.Allowed, true, true));
        return (run, chat, now);
    }
}
