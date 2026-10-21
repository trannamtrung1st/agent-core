using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareExperience(Guid id, Guid instance, PreparedExperienceOperation value, long revision, long persona)
    {
        var args = TenantExperienceToolContract.Parse(value.ToolName, value.ArgumentsJson); var sources = TenantExperienceRules.Sources(value);
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty || value.UserPrincipalId == Guid.Empty
            || revision < 1 || persona < 1 || value.ExperienceRevision < 1 || value.MaintenanceRevision < 0 || args.CanonicalArguments != value.ArgumentsJson
            || Encoding.UTF8.GetByteCount(value.SourcesJson) > 8192 || Encoding.UTF8.GetByteCount(value.SourceLabelsJson) > 8192
            || args.ReadOnly && value.ResultExperienceId is not null || !args.ReadOnly && value.ResultExperienceId is not { } resultId
            || value.ResultExperienceId == Guid.Empty || args.Tool == TenantExperienceToolContract.Consolidate && (value.Source is not null || sources.Length is < 2 or > 8)
            || args.Tool != TenantExperienceToolContract.Consolidate && (sources.Length != 0 || value.Source is null)) throw new ArgumentException("Use a bounded exact Experience operation.");
        if (value.Source is { } source)
        {
            TenantExperienceRules.SourceShape(source);
            if (source.Kind != args.SourceKind || source.SourceId != args.SourceId || args.ThroughCursor is { } cursor && source.ThroughCursor != cursor)
                throw new ArgumentException("Use the observed Experience source and cursor.");
        }
        if (args.Content is { } content) _ = TenantExperienceRules.Freeze(content);
        if (value.ObservationHash != TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, source = value.Source, sources = value.SourcesJson })))
            throw new ArgumentException("Use the exact Experience observation hash.");
        using var labels = JsonDocument.Parse(value.SourceLabelsJson);
        if (labels.RootElement.ValueKind != JsonValueKind.Array || labels.RootElement.GetArrayLength() != (value.Source is null ? sources.Length : 1)) throw new ArgumentException("Use exact Experience source labels.");
        foreach (var label in labels.RootElement.EnumerateArray())
        {
            TenantApprovalToolContract.Require(label, ["id", "title", "kind"]);
            if (label.GetProperty("id").ValueKind != JsonValueKind.String || label.GetProperty("title").ValueKind != JsonValueKind.String
                || label.GetProperty("title").GetString()!.Length > 160 || label.GetProperty("kind").ValueKind != JsonValueKind.String) throw new ArgumentException("Use bounded Experience labels.");
        }
        var json = JsonSerializer.Serialize(new { operation = "experience.operation", experience = value, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        return new(id, instance, null, revision, persona, json, TenantMemoryOperationRules.Hash(json), ExperienceOperation: value);
    }
}
