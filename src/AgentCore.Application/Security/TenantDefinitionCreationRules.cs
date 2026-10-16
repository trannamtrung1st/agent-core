using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public static class TenantDefinitionCreationRules
{
    public static void Validate(TenantDefinition root, AgentDefinitionDraft draft, IReadOnlyList<AgentDefinitionDraftResource> resources)
    {
        if (root.DeletedAt is not null || root.DefinitionId == Guid.Empty || draft.DefinitionId != root.DefinitionId.ToString("D")
            || draft.Candidate.DefinitionId != draft.DefinitionId || draft.Revision != 1) throw new ArgumentException("Invalid definition ownership.");
        var validSource = draft.SourceKind switch
        {
            DefinitionDraftSourceKind.New => draft.SourceVersion is null && draft.SourceDefinitionId is null && resources.Count == 0,
            DefinitionDraftSourceKind.ForkBuiltIn => draft.SourceVersion is > 0 && !string.IsNullOrWhiteSpace(draft.SourceDefinitionId)
                && draft.SourceDefinitionId.Length <= 64 && draft.SourceDefinitionId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') && draft.SourceDefinitionId != draft.DefinitionId,
            _ => false
        };
        if (!validSource) throw new ArgumentException("Invalid definition source.");
        if (resources.Count > 0) TenantDefinitionChildRules.ValidateResourceBatch(draft.DraftId, [], resources);
    }
    public static bool SameSource(AgentDefinitionDraft before, AgentDefinitionDraft after) =>
        before.SourceKind == after.SourceKind && before.SourceVersion == after.SourceVersion && before.SourceDefinitionId == after.SourceDefinitionId
        && before.CreatedAt.ToUnixTimeMilliseconds() == after.CreatedAt.ToUnixTimeMilliseconds();
}
