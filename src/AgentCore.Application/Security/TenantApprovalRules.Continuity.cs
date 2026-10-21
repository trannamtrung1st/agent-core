using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareContinuity(Guid id, Guid instance, PreparedContinuityRead value, long revision, long personaRevision)
    {
        var args = TenantContinuityToolContract.Parse(value.ToolName, value.ArgumentsJson); var sessions = TenantContinuityRules.Sessions(value);
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty || value.UserPrincipalId == Guid.Empty || revision < 1 || personaRevision < 1
            || args.CanonicalArguments != value.ArgumentsJson || !TenantMemoryOperationRules.ValidHash(value.ObservationHash)
            || System.Text.Encoding.UTF8.GetByteCount(value.SessionsJson) > 16384 || System.Text.Encoding.UTF8.GetByteCount(value.SourceLabelsJson) > 8192
            || args.Kind == "Session" && (sessions.Length != 1 || sessions[0].SessionId != args.Id || sessions[0].AfterEntrySequence != args.AfterEntrySequence || sessions[0].Limit != args.Limit)
            || args.Kind == "Memory" && (sessions.Length != 0 || value.Memory is null)
            || value.Memory is { } m && (m.SessionId != value.SessionId || m.RootSessionId != value.RootSessionId || m.UserPrincipalId != value.UserPrincipalId || m.ToolName != args.Tool || m.ArgumentsJson != args.CanonicalArguments
                || !TenantMemoryToolContract.ReadOnly(m.ToolName)) || args.Tool == ToolCatalog.ContinuitySearch && sessions.Length > args.Limit + 1)
            throw new ArgumentException("Use a bounded exact continuity observation.");
        if (value.Memory is { } memory) _ = PrepareMemory(id, instance, memory, revision, personaRevision);
        using var labels = JsonDocument.Parse(value.SourceLabelsJson);
        if (labels.RootElement.ValueKind != JsonValueKind.Array || labels.RootElement.GetArrayLength() != sessions.Length) throw new ArgumentException("Use exact historical source labels.");
        var n = 0;
        foreach (var label in labels.RootElement.EnumerateArray())
        {
            TenantApprovalToolContract.Require(label, ["sessionId", "title", "throughEntrySequence", "afterEntrySequence", "limit"]); var source = sessions[n++];
            if (label.GetProperty("sessionId").GetGuid() != source.SessionId || label.GetProperty("throughEntrySequence").GetInt64() != source.ThroughEntrySequence
                || label.GetProperty("afterEntrySequence").GetInt64() != source.AfterEntrySequence || label.GetProperty("limit").GetInt32() != source.Limit
                || label.GetProperty("title").ValueKind != JsonValueKind.String || label.GetProperty("title").GetString()!.Length > 160) throw new ArgumentException("Invalid historical source label.");
        }
        if (value.ObservationHash != TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, sessions = value.SessionsJson, memory = value.Memory }))) throw new ArgumentException("Invalid continuity observation hash.");
        var json = JsonSerializer.Serialize(new { operation = "continuity.read", continuity = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, revision, personaRevision, json, TenantMemoryOperationRules.Hash(json), ContinuityRead: value);
    }
}
