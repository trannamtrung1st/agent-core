using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftValidationService(
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionResourceService resources,
    ProviderAliasSet aliases,
    IModelCatalog catalog,
    IToolConfigurationGate configurationGate)
{
    public async ValueTask<DefinitionValidationResult> ValidateDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var revisionSnapshot = draft.Revision;
        var findings = new List<DefinitionValidationFinding>();
        findings.AddRange(
            AgentDefinitionCandidateValidator.CollectPublicationFindings(
                draft.Candidate,
                aliases,
                catalog,
                configurationGate));
        if (!findings.Any(finding => finding.Severity == DefinitionValidationSeverity.Blocking))
        {
            var draftResources = await resources.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
            draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
            if (draft.Revision != revisionSnapshot)
            {
                throw AgentCoreErrors.Conflict("Draft changed during validation; retry validation.");
            }

            findings.AddRange(DefinitionDraftResourceValidation.CollectFindings(draft.Candidate, draftResources));
        }

        return new DefinitionValidationResult(
            draft.DraftId,
            draft.Revision,
            findings.Any(finding => finding.Severity == DefinitionValidationSeverity.Blocking),
            findings);
    }
}
