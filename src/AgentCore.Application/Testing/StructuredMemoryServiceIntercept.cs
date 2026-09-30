using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Testing;

/// <summary>
/// Delegates to an inner structured-memory service, with optional promotion failures for admission tests.
/// </summary>
public sealed class StructuredMemoryServiceIntercept(IStructuredMemoryService inner) : IStructuredMemoryService
{
    public bool BlockIdentityPromotion { get; init; }
    public bool BlockUserPromotion { get; init; }

    public ValueTask<StructuredMemoryItem> WriteAsync(
        TrustedMemoryOwner owner,
        MemoryWriteProposal proposal,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.WriteAsync(owner, proposal, admission, cancellationToken);

    public ValueTask<StructuredMemoryItem> UpdateAsync(
        TrustedMemoryOwner owner,
        MemoryUpdateProposal proposal,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.UpdateAsync(owner, proposal, admission, cancellationToken);

    public ValueTask<StructuredMemoryItem> DeleteAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(owner, memoryId, cancellationToken);

    public ValueTask<StructuredMemoryItem?> GetAsync(
        TrustedMemoryOwner owner,
        Guid memoryId,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.GetAsync(owner, memoryId, admission, cancellationToken);

    public ValueTask<IReadOnlyList<StructuredMemoryItem>> SearchAsync(
        TrustedMemoryOwner owner,
        MemorySearchQuery query,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.SearchAsync(owner, query, admission, cancellationToken);

    public ValueTask<StructuredMemoryItem?> FindActiveBySubjectAsync(
        TrustedMemoryOwner owner,
        MemoryKind kind,
        string subject,
        CancellationToken cancellationToken = default) =>
        inner.FindActiveBySubjectAsync(owner, kind, subject, cancellationToken);

    public ValueTask<StructuredMemoryItem?> FindActiveIdentityUserBySubjectAsync(
        TrustedIdentityUserOwner owner,
        MemoryKind kind,
        string subject,
        CancellationToken cancellationToken = default) =>
        inner.FindActiveIdentityUserBySubjectAsync(owner, kind, subject, cancellationToken);

    public ValueTask<StructuredMemoryItem?> FindActiveUserBySubjectAsync(
        TrustedUserOwner owner,
        MemoryKind kind,
        string subject,
        CancellationToken cancellationToken = default) =>
        inner.FindActiveUserBySubjectAsync(owner, kind, subject, cancellationToken);

    public ValueTask<StructuredMemoryItem> PromoteToIdentityUserAsync(
        TrustedMemoryOwner session,
        Guid memoryId,
        TrustedIdentityUserOwner destination,
        bool promotionAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default)
    {
        if (BlockIdentityPromotion)
        {
            throw new AgentCoreException("PolicyDenied", "Identity promotion blocked for test.", 403);
        }

        return inner.PromoteToIdentityUserAsync(
            session,
            memoryId,
            destination,
            promotionAllowed,
            admission,
            cancellationToken);
    }

    public ValueTask<StructuredMemoryItem> UpdateIdentityUserAsync(
        TrustedIdentityUserOwner owner,
        MemoryUpdateProposal proposal,
        bool retrievalAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.UpdateIdentityUserAsync(owner, proposal, retrievalAllowed, admission, cancellationToken);

    public ValueTask<StructuredMemoryItem> DeleteIdentityUserAsync(
        TrustedIdentityUserOwner owner,
        Guid memoryId,
        bool retrievalAllowed,
        CancellationToken cancellationToken = default) =>
        inner.DeleteIdentityUserAsync(owner, memoryId, retrievalAllowed, cancellationToken);

    public ValueTask<IReadOnlyList<StructuredMemoryItem>> SearchIdentityUserAsync(
        TrustedIdentityUserOwner owner,
        MemorySearchQuery query,
        bool retrievalAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.SearchIdentityUserAsync(owner, query, retrievalAllowed, admission, cancellationToken);

    public ValueTask<StructuredMemoryItem> PromoteSessionToUserAsync(
        TrustedMemoryOwner session,
        Guid memoryId,
        TrustedUserOwner destination,
        bool promotionAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default)
    {
        if (BlockUserPromotion)
        {
            throw new AgentCoreException("PolicyDenied", "User promotion blocked for test.", 403);
        }

        return inner.PromoteSessionToUserAsync(
            session,
            memoryId,
            destination,
            promotionAllowed,
            admission,
            cancellationToken);
    }

    public ValueTask<StructuredMemoryItem> PromoteIdentityUserToUserAsync(
        TrustedIdentityUserOwner source,
        Guid memoryId,
        TrustedUserOwner destination,
        bool promotionAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.PromoteIdentityUserToUserAsync(
            source,
            memoryId,
            destination,
            promotionAllowed,
            admission,
            cancellationToken);

    public ValueTask<StructuredMemoryItem> UpdateUserAsync(
        TrustedUserOwner owner,
        MemoryUpdateProposal proposal,
        bool retrievalAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.UpdateUserAsync(owner, proposal, retrievalAllowed, admission, cancellationToken);

    public ValueTask<StructuredMemoryItem> DeleteUserAsync(
        TrustedUserOwner owner,
        Guid memoryId,
        bool retrievalAllowed,
        CancellationToken cancellationToken = default) =>
        inner.DeleteUserAsync(owner, memoryId, retrievalAllowed, cancellationToken);

    public ValueTask<IReadOnlyList<StructuredMemoryItem>> SearchUserAsync(
        TrustedUserOwner owner,
        MemorySearchQuery query,
        bool retrievalAllowed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken = default) =>
        inner.SearchUserAsync(owner, query, retrievalAllowed, admission, cancellationToken);

    public ValueTask<int> ResetSessionScopeAsync(
        TrustedMemoryOwner owner,
        CancellationToken cancellationToken = default) =>
        inner.ResetSessionScopeAsync(owner, cancellationToken);

    public ValueTask<int> ResetIdentityUserScopeAsync(
        TrustedIdentityUserOwner owner,
        bool retrievalAllowed,
        CancellationToken cancellationToken = default) =>
        inner.ResetIdentityUserScopeAsync(owner, retrievalAllowed, cancellationToken);

    public ValueTask<int> ResetUserScopeAsync(
        TrustedUserOwner owner,
        bool retrievalAllowed,
        CancellationToken cancellationToken = default) =>
        inner.ResetUserScopeAsync(owner, retrievalAllowed, cancellationToken);
}
