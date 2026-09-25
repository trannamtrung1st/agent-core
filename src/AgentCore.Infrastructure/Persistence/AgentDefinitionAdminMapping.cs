using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

internal static class AgentDefinitionAdminMapping
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static AgentDefinitionDraft MapDraft(AgentDefinitionDraftRecord row) =>
        new(
            Guid.Parse(row.DraftId),
            row.DefinitionId,
            row.Revision,
            DeserializeCandidate(row.CandidateJson),
            (DefinitionDraftSourceKind)row.SourceKind,
            row.SourceVersion,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));

    internal static AgentDefinitionDraftRecord MapDraft(AgentDefinitionDraft draft) =>
        new()
        {
            DraftId = draft.DraftId.ToString("D"),
            DefinitionId = draft.DefinitionId,
            Revision = draft.Revision,
            CandidateJson = SerializeCandidate(draft.Candidate),
            SourceKind = (int)draft.SourceKind,
            SourceVersion = draft.SourceVersion,
            CreatedAtUtc = draft.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = draft.UpdatedAt.ToUnixTimeMilliseconds()
        };

    internal static AgentDefinitionPublication MapPublication(AgentDefinitionPublicationRecord row) =>
        new(
            row.DefinitionId,
            row.Version,
            DeserializeDefinition(row.PayloadJson),
            row.SourceDraftRevision,
            (DefinitionPublicationStatus)row.Status,
            row.MetadataRevision,
            DateTimeOffset.FromUnixTimeMilliseconds(row.PublishedAtUtc));

    internal static AgentDefinitionPublicationRecord MapPublication(AgentDefinitionPublication publication) =>
        new()
        {
            DefinitionId = publication.DefinitionId,
            Version = publication.Version,
            PayloadJson = SerializeDefinition(publication.Payload),
            SourceDraftRevision = publication.SourceDraftRevision,
            Status = (int)publication.Status,
            MetadataRevision = publication.MetadataRevision,
            PublishedAtUtc = publication.PublishedAt.ToUnixTimeMilliseconds()
        };

    private static string SerializeCandidate(AgentDefinitionCandidate candidate) =>
        JsonSerializer.Serialize(candidate, Json);

    private static AgentDefinitionCandidate DeserializeCandidate(string json) =>
        JsonSerializer.Deserialize<AgentDefinitionCandidate>(json, Json)
        ?? throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Stored definition candidate was empty.");

    private static string SerializeDefinition(AgentDefinition definition) =>
        JsonSerializer.Serialize(definition, Json);

    private static AgentDefinition DeserializeDefinition(string json) =>
        JsonSerializer.Deserialize<AgentDefinition>(json, Json)
        ?? throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Stored definition publication was empty.");
}
