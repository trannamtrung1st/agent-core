using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftValidationService(
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionResourceService resources,
    ProviderAliasSet aliases,
    IModelCatalog catalog,
    IToolConfigurationGate configurationGate, ISandboxStore? sandboxes = null, ISandboxImageAuthority? sandboxAuthority = null, ISandboxReadiness? sandboxReadiness = null)
{
    public async ValueTask<DefinitionValidationResult> ValidateDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var revisionSnapshot = draft.Revision;
        var findings = new List<DefinitionValidationFinding>();
        var source = await lifecycle.GetDraftSourceAsync(draft, cancellationToken).ConfigureAwait(false);
        findings.AddRange(
            AgentDefinitionCandidateValidator.CollectPublicationFindings(
                draft.Candidate,
                aliases,
                catalog,
                configurationGate,
                source is null ? [] : RoleEnvironments.Of(source).ToolList));
        if (draft.Candidate.SandboxPolicy is { } policy)
            foreach (var id in policy.AllowedProfileIds)
            {
                var profile = sandboxes is null ? null : await sandboxes.GetProfileAsync(id, ct:cancellationToken);
                if (sandboxReadiness?.State != "Ready" || profile is null || sandboxAuthority is null || !(await sandboxAuthority.InspectAsync(profile, cancellationToken)).Eligible)
                    findings.Add(new("sandboxPolicy", "sandbox_profile_unavailable", $"Profile '{id}' must be authorized and verified. Repair the selection in Sandboxes before publishing.", DefinitionValidationSeverity.Blocking));
            }
        var draftResources = await resources.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
        draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft.Revision != revisionSnapshot)
        {
            throw AgentCoreErrors.Conflict("Draft changed during validation; retry validation.");
        }

        if (!findings.Any(finding => finding.Severity == DefinitionValidationSeverity.Blocking))
        {
            findings.AddRange(DefinitionDraftResourceValidation.CollectFindings(draft.Candidate, draftResources));
        }

        var fingerprint = DefinitionDraftConfigurationFingerprint.Compute(draft.Candidate, draftResources);
        return new DefinitionValidationResult(
            draft.DraftId,
            draft.Revision,
            fingerprint,
            findings.Any(finding => finding.Severity == DefinitionValidationSeverity.Blocking),
            findings);
    }
}
