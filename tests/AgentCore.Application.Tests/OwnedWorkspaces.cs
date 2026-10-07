using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Application.Tests;

/// <summary>Owned Session metadata for isolated workspace port tests. HTTP/runtime ownership uses its real store.</summary>
internal static class OwnedWorkspaces
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "owned-workspace-unit-" + Guid.NewGuid().ToString("N"));

    public static AgentInstanceWorkspaceService Create(ISessionWorkspace scratch)
    {
        var time = TimeProvider.System;
        var ids = new SystemIdGenerator(time);
        var owner = Guid.NewGuid();
        var now = time.GetUtcNow();
        var instances = new InMemoryAgentInstanceStore();
        instances.InsertAsync(new AgentInstance(owner, "examiner", 1, SampleDefinitions.Examiner.Identity,
            AgentInstanceLifecycle.Active, now, now)).AsTask().GetAwaiter().GetResult();
        return new(instances, new FileAgentInstanceWorkspaceStore(Root, time, ids),
            new SessionMetadata(owner), scratch, new AdminLifecycleCoordinator());
    }

    private sealed class SessionMetadata(Guid owner) : IMemoryStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult<SessionSnapshot?>(new SessionSnapshot(1, sessionId, 1, SampleDefinitions.Examiner,
                SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now, owner));
        }
        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(Guid sessionId, long afterEntrySequence, int limit, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ConversationEntry>>([]);
        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => ValueTask.FromResult<UserProfile?>(null);
        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
