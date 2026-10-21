using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareMemory(Guid id, Guid instance, PreparedMemoryOperation value, long revision, long personaRevision)
    {
        var args = TenantMemoryToolContract.Parse(value.ToolName, value.ArgumentsJson); var sources = TenantMemoryOperationRules.Sources(value);
        if (System.Text.Encoding.UTF8.GetByteCount(value.SourceLabelsJson) > 16384) throw new ArgumentException("Use bounded Memory source labels.");
        using var labels = JsonDocument.Parse(value.SourceLabelsJson);
        if (labels.RootElement.ValueKind != JsonValueKind.Array || labels.RootElement.GetArrayLength() != sources.Length) throw new ArgumentException("Use exact Memory labels.");
        var index = 0;
        foreach (var label in labels.RootElement.EnumerateArray())
        {
            TenantApprovalToolContract.Require(label, ["memoryId", "scope", "kind", "subject"]);
            if (label.GetProperty("memoryId").GetGuid() != sources[index++].MemoryId || label.GetProperty("scope").GetString() is not ("session" or "identityUser" or "user")
                || !Enum.TryParse<AgentCore.Domain.Memory.MemoryKind>(label.GetProperty("kind").GetString(), false, out var kind) || !Enum.IsDefined(kind)
                || label.GetProperty("subject").ValueKind != JsonValueKind.String || label.GetProperty("subject").GetString()!.Length > 128) throw new ArgumentException("Invalid exact Memory labels.");
        }
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty || value.UserPrincipalId == Guid.Empty || revision < 1 || personaRevision < 1
            || args.CanonicalArguments != value.ArgumentsJson || !TenantMemoryOperationRules.ValidHash(value.ObservationHash)
            || (args.Tool == ToolCatalog.MemoryConsolidate) != (value.ResultMemoryId is not null) || value.ResultMemoryId == Guid.Empty
            || args.Tool != ToolCatalog.ContinuitySearch && !sources.Select(s => s.MemoryId).SequenceEqual(args.SourceIds ?? [args.MemoryId!.Value])
            || args.Tool == ToolCatalog.ContinuitySearch && sources.Length > args.Limit + 1
            || value.ObservationHash != TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { args.CanonicalArguments, sourceJson = value.SourcesJson, resultId = value.ResultMemoryId })))
            throw new ArgumentException("Use exact canonical Memory operation witnesses.");
        var json = JsonSerializer.Serialize(new { operation = "memory.operation", memory = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, revision, personaRevision, json, TenantMemoryOperationRules.Hash(json), MemoryOperation: value);
    }
}
