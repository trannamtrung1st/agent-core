using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Tests.Shared;

/// <summary>Explicit trusted metadata stub for isolated filesystem-port tests, independent of HTTP provisioning.</summary>
internal sealed class WorkspaceTestSessions : IMemoryStore
{
    private static readonly AgentDefinition Definition = new(1,"examiner",1,new AgentIdentity("Alex","Examiner","Practice.","Calm"),["Practice"],"Instructions",new BehaviorPolicy("acknowledgeThenContinue",true,true),new ConversationPolicy("concise",true,"en",256),new InitiativePolicy(true,8000,30000,1,["longSilence"]),new VoiceConfiguration(true,"default",1),new ProviderPreferences("primary-llm","primary-stt","primary-tts"),new Dictionary<string,string>());
    public Guid Owner { get; } = Guid.NewGuid();
    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult<SessionSnapshot?>(new(1, sessionId, 1, Definition,
            SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now, Owner));
    }
    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(Guid sessionId, long afterEntrySequence, int limit, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ConversationEntry>>([]);
    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => ValueTask.FromResult<UserProfile?>(null);
    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
