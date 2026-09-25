using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftPublishService(
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionDraftValidationService validation,
    AgentDefinitionDraftEvaluationService evaluation,
    AgentDefinitionDraftDiffService diff,
    IIdGenerator ids)
{
    public async ValueTask<AgentDefinitionPublication> PublishDraftAsync(
        Guid draftId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        var validationResult = await validation.ValidateDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (validationResult.HasBlockingFindings)
        {
            var blocking = validationResult.Findings.First(finding =>
                finding.Severity == DefinitionValidationSeverity.Blocking);
            throw AgentCoreErrors.Validation(blocking.Message);
        }

        if (validationResult.DraftRevision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft changed during publish validation; retry publish.");
        }

        await evaluation.EnsureRequiredEvidenceAsync(
            draftId,
            validationResult.DraftRevision,
            validationResult.ConfigurationFingerprint,
            cancellationToken).ConfigureAwait(false);

        var diffResult = await diff.GetDraftDiffAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (diffResult.DraftRevision != validationResult.DraftRevision)
        {
            throw AgentCoreErrors.Conflict("Draft changed during publish diff; retry publish.");
        }

        var changedSectionIds = diffResult.Sections
            .Where(section => section.ChangeKind != DefinitionDiffChangeKind.Unchanged)
            .Select(section => section.SectionId)
            .ToArray();

        return await lifecycle.CommitDraftPublicationAsync(
            draftId,
            validationResult.DraftRevision,
            ids.NewId(),
            changedSectionIds,
            cancellationToken).ConfigureAwait(false);
    }
}
