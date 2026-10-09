using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Memory;

public static class MemoryReceiptProjection
{
    public static IReadOnlyList<MemoryReceipt> FromResults(IReadOnlyList<MemoryAdmissionResult> results)
    {
        if (results.Count == 0)
        {
            return [];
        }

        var receipts = new MemoryReceipt[results.Count];
        for (var index = 0; index < results.Count; index++)
        {
            receipts[index] = FromResult(results[index]);
        }

        return receipts;
    }

    public static MemoryReceipt FromResult(MemoryAdmissionResult result)
    {
        var proposal = result.Proposal;
        var operation = proposal.Operation switch
        {
            MemoryProposalOperation.Delete => "delete",
            MemoryProposalOperation.Resolve => "resolve",
            _ => "upsert"
        };
        return new MemoryReceipt(
            Outcome(result.Status),
            operation,
            Source(proposal.Source),
            proposal.Subject.Trim(),
            result.AffectedScopes,
            Presentation(proposal.Source, proposal.Operation, result.Status));
    }

    private static string Presentation(
        MemoryProposalSource source,
        MemoryProposalOperation operation,
        MemoryAdmissionStatus status)
    {
        if (operation == MemoryProposalOperation.Delete
            && MemoryProposalCodec.IsConversationalSource(source) && status != MemoryAdmissionStatus.Deleted)
            return MemoryReceipt.Explicit;

        if (source != MemoryProposalSource.UserExplicit)
        {
            return MemoryReceipt.Silent;
        }

        if (operation == MemoryProposalOperation.Delete)
        {
            return status == MemoryAdmissionStatus.Deleted
                ? MemoryReceipt.Indicator
                : MemoryReceipt.Explicit;
        }

        if (operation == MemoryProposalOperation.Resolve)
            return status is MemoryAdmissionStatus.Resolved or MemoryAdmissionStatus.AlreadyResolved
                ? MemoryReceipt.Indicator : MemoryReceipt.Explicit;

        return status is MemoryAdmissionStatus.Stored
            or MemoryAdmissionStatus.Updated
            or MemoryAdmissionStatus.AlreadyStored
            or MemoryAdmissionStatus.StoredSessionOnly
            or MemoryAdmissionStatus.UpdatedSessionOnly
            ? MemoryReceipt.Indicator
            : MemoryReceipt.Explicit;
    }

    private static string Outcome(MemoryAdmissionStatus status) => status switch
    {
        MemoryAdmissionStatus.Stored => "stored",
        MemoryAdmissionStatus.Updated => "updated",
        MemoryAdmissionStatus.AlreadyStored => "alreadyStored",
        MemoryAdmissionStatus.StoredSessionOnly => "storedSessionOnly",
        MemoryAdmissionStatus.UpdatedSessionOnly => "updatedSessionOnly",
        MemoryAdmissionStatus.Deleted => "deleted",
        MemoryAdmissionStatus.Unavailable => "unavailable",
        MemoryAdmissionStatus.Rejected => "rejected",
        MemoryAdmissionStatus.ApprovalRequired => "approvalRequired",
        MemoryAdmissionStatus.NotFound => "notFound",
        MemoryAdmissionStatus.PartiallyDeleted => "partiallyDeleted",
        MemoryAdmissionStatus.Resolved => "resolved",
        MemoryAdmissionStatus.AlreadyResolved => "alreadyResolved",
        _ => "rejected"
    };

    private static string Source(MemoryProposalSource source) => source switch
    {
        MemoryProposalSource.UserExplicit => "userExplicit",
        MemoryProposalSource.AgentInferred => "agentInferred",
        MemoryProposalSource.Application => "application",
        MemoryProposalSource.Admin => "admin",
        _ => "agentInferred"
    };
}
