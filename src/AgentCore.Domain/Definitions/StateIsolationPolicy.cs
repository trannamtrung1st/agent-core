using System.Text.Json.Serialization;

namespace AgentCore.Domain.Definitions;

[JsonConverter(typeof(JsonStringEnumConverter<ConversationContextScope>))]
public enum ConversationContextScope { Session, CurrentRequest }
[JsonConverter(typeof(JsonStringEnumConverter<HomeAccess>))]
public enum HomeAccess { ReadWrite, ReadOnly, Disabled }
[JsonConverter(typeof(JsonStringEnumConverter<WorkingCleanup>))]
public enum WorkingCleanup { OnSessionDeletion, AfterTerminal }
[JsonConverter(typeof(JsonStringEnumConverter<BrowserIsolation>))]
public enum BrowserIsolation { HostDefault, PersistentAgent, EphemeralSession }
[JsonConverter(typeof(JsonStringEnumConverter<AgentStateVisibility>))]
public enum AgentStateVisibility { Off, CurrentSession, InstanceSummary }

/// <summary>Ordinary independently editable policy; no persisted preset/runtime mode.</summary>
public sealed record StateIsolationPolicy(
    ConversationContextScope ConversationContextScope = ConversationContextScope.Session,
    HomeAccess HomeAccess = HomeAccess.ReadWrite,
    WorkingCleanup WorkingCleanup = WorkingCleanup.OnSessionDeletion,
    BrowserIsolation BrowserIsolation = BrowserIsolation.HostDefault,
    AgentStateVisibility AgentStateVisibility = AgentStateVisibility.Off,
    bool ExperienceRecord = true,
    bool ExperienceRecall = true,
    bool ProfileEvidence = true,
    bool CrossSessionContinuity = true)
{
    public static StateIsolationPolicy Default { get; } = new();

    public void Validate()
    {
        if (!Enum.IsDefined(ConversationContextScope) || !Enum.IsDefined(HomeAccess) || !Enum.IsDefined(WorkingCleanup)
            || !Enum.IsDefined(BrowserIsolation) || !Enum.IsDefined(AgentStateVisibility))
            throw new ArgumentException("State and isolation policy contains an unknown value.");
    }

    public static StateIsolationPolicy Restrict(StateIsolationPolicy admitted, StateIsolationPolicy current) => admitted with {
        ConversationContextScope = (ConversationContextScope)Math.Max((int)admitted.ConversationContextScope, (int)current.ConversationContextScope),
        HomeAccess = (HomeAccess)Math.Max((int)admitted.HomeAccess, (int)current.HomeAccess),
        BrowserIsolation = admitted.BrowserIsolation == BrowserIsolation.EphemeralSession || current.BrowserIsolation == BrowserIsolation.EphemeralSession
            ? BrowserIsolation.EphemeralSession : admitted.BrowserIsolation,
        AgentStateVisibility = (AgentStateVisibility)Math.Min((int)admitted.AgentStateVisibility, (int)current.AgentStateVisibility),
        ExperienceRecord = admitted.ExperienceRecord && current.ExperienceRecord,
        ExperienceRecall = admitted.ExperienceRecall && current.ExperienceRecall,
        ProfileEvidence = admitted.ProfileEvidence && current.ProfileEvidence,
        CrossSessionContinuity = admitted.CrossSessionContinuity && current.CrossSessionContinuity
    };
}
