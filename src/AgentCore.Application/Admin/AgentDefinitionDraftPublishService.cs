using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftPublishService(
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionDraftValidationService validation)
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

        return await lifecycle.CommitDraftPublicationAsync(
            draftId,
            validationResult.DraftRevision,
            cancellationToken).ConfigureAwait(false);
    }
}
