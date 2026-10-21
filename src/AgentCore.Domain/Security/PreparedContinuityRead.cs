using System.Text.Json.Serialization;

namespace AgentCore.Domain.Security;

// Exact historical observations share the incumbent approval/Run substrate. Values
// are never execution authority, and the saved action never selects another actor.
public sealed record PreparedHistoricalSession(Guid SessionId, long Revision, long ThroughEntrySequence, long AfterEntrySequence, int Limit, string ObservationHash);
public sealed record PreparedContinuityRead(Guid SessionId, Guid RootSessionId, Guid? UserPrincipalId,
    string ToolName, string ArgumentsJson, string SessionsJson, string ObservationHash, string SourceLabelsJson,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedMemoryOperation? Memory = null);
