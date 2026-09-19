using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionLifecycleTransitionTests
{
    [Fact]
    public async Task Pause_resume_complete_and_repeat_complete_follow_the_graph()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        Assert.Equal(SessionLifecycleStatus.Active, created.LifecycleStatus);

        var paused = await manager.DeactivateAsync(created.SessionId);
        Assert.Equal(SessionStatus.Paused, paused.Status);
        Assert.Equal(SessionLifecycleStatus.Paused, paused.LifecycleStatus);

        var resumed = await manager.ReopenAsync(created.SessionId);
        Assert.Equal(SessionStatus.Created, resumed.Status);
        Assert.Equal(SessionLifecycleStatus.Active, resumed.LifecycleStatus);

        var completed = await manager.TransitionLifecycleAsync(
            created.SessionId,
            SessionLifecycleStatus.Completed,
            LifecycleTransitionSource.User,
            reason: "done");
        Assert.Equal(SessionStatus.Ended, completed.Status);
        Assert.Equal(SessionLifecycleStatus.Completed, completed.LifecycleStatus);

        var again = await manager.TransitionLifecycleAsync(
            created.SessionId,
            SessionLifecycleStatus.Completed,
            LifecycleTransitionSource.User);
        Assert.Equal(completed.Revision, again.Revision);

        var mismatch = await Assert.ThrowsAsync<AgentCoreException>(() =>
            manager.TransitionLifecycleAsync(
                created.SessionId,
                SessionLifecycleStatus.Cancelled,
                LifecycleTransitionSource.Host));
        Assert.Equal("ValidationError", mismatch.Code);

        var reactivate = await Assert.ThrowsAsync<AgentCoreException>(() =>
            manager.TransitionLifecycleAsync(
                created.SessionId,
                SessionLifecycleStatus.Active,
                LifecycleTransitionSource.Host));
        Assert.Equal("ValidationError", reactivate.Code);
    }

    [Fact]
    public async Task Pause_preserves_user_host_and_system_provenance()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var userSession = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var userPaused = await manager.DeactivateAsync(userSession.SessionId);
        Assert.Equal(LifecycleTransitionSource.User, userPaused.LifecycleSource);
        Assert.Equal("manual", userPaused.LifecycleReason);
        Assert.NotNull(userPaused.LifecycleChangedAt);

        var hostSession = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var hostPaused = await manager.TransitionLifecycleAsync(
            hostSession.SessionId,
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.Host,
            reason: "proctor-hold");
        Assert.Equal(LifecycleTransitionSource.Host, hostPaused.LifecycleSource);
        Assert.Equal("proctor-hold", hostPaused.LifecycleReason);

        var systemSession = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var systemPaused = await manager.TransitionLifecycleAsync(
            systemSession.SessionId,
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.System,
            reason: "inactivity");
        Assert.Equal(LifecycleTransitionSource.System, systemPaused.LifecycleSource);
        Assert.Equal("inactivity", systemPaused.LifecycleReason);
    }

    [Fact]
    public async Task Active_to_active_is_idempotent_on_the_store()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        var again = await manager.TransitionLifecycleAsync(
            created.SessionId,
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User);
        Assert.Equal(created.Revision, again.Revision);
        Assert.Equal(SessionLifecycleStatus.Active, again.LifecycleStatus);
        Assert.Equal(SessionStatus.Created, again.Status);
    }

    [Fact]
    public async Task User_complete_respects_policy_while_host_remains_allowed()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            purpose: null,
            policy: new SessionCompletionPolicy(AgentCompletionAuthority.Disabled, false, true));
        var denied = await Assert.ThrowsAsync<AgentCoreException>(() =>
            manager.TransitionLifecycleAsync(
                created.SessionId,
                SessionLifecycleStatus.Completed,
                LifecycleTransitionSource.User));
        Assert.Equal("Forbidden", denied.Code);

        var host = await manager.TransitionLifecycleAsync(
            created.SessionId,
            SessionLifecycleStatus.Completed,
            LifecycleTransitionSource.Host);
        Assert.Equal(SessionLifecycleStatus.Completed, host.LifecycleStatus);
    }

    [Fact]
    public async Task Detached_get_and_create_expire_when_deadline_is_already_past()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 3, 0, 0, TimeSpan.Zero));
        var manager = CreateManager(new InMemoryMemoryStore(), time);
        var created = await manager.CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            purpose: new SessionPurpose(SessionPurposeKind.Goal, DeadlineAt: time.GetUtcNow().AddMinutes(5)));
        Assert.Equal(SessionLifecycleStatus.Active, created.LifecycleStatus);

        time.Advance(TimeSpan.FromMinutes(6));
        var expired = await manager.GetAsync(created.SessionId);
        Assert.Equal(SessionStatus.Ended, expired.Status);
        Assert.Equal(SessionLifecycleStatus.Expired, expired.LifecycleStatus);
        Assert.Equal("deadline", expired.LifecycleReason);

        var past = await manager.CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            purpose: new SessionPurpose(SessionPurposeKind.Goal, DeadlineAt: time.GetUtcNow().AddMinutes(-1)));
        Assert.Equal(SessionLifecycleStatus.Expired, past.LifecycleStatus);
        Assert.Equal(SessionStatus.Ended, past.Status);
    }

    private static SessionManager CreateManager(IMemoryStore store, TimeProvider? time = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b8{index:D2}")).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)),
            new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
