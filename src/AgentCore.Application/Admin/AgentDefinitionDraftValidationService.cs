using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftValidationService(
    AgentDefinitionLifecycleService lifecycle,
    ProviderAliasSet aliases,
    IModelCatalog catalog,
    IToolConfigurationGate configurationGate)
{
    public async ValueTask<DefinitionValidationResult> ValidateDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var findings = AgentDefinitionCandidateValidator.CollectPublicationFindings(
            draft.Candidate,
            aliases,
            catalog,
            configurationGate);
        return new DefinitionValidationResult(
            draft.DraftId,
            draft.Revision,
            findings.Any(finding => finding.Severity == DefinitionValidationSeverity.Blocking),
            findings);
    }
}
