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
        var operation = proposal.Operation == MemoryProposalOperation.Delete ? "delete" : "upsert";
        return new MemoryReceipt(
            Outcome(result.Status),
            operation,
            Source(proposal.Source),
            proposal.Subject.Trim(),
            Scope(result.Status, proposal.ScopeHint),
            Presentation(proposal.Source, proposal.Operation, result.Status));
    }

    private static string Presentation(
        MemoryProposalSource source,
        MemoryProposalOperation operation,
        MemoryAdmissionStatus status)
    {
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

        return status is MemoryAdmissionStatus.Stored
            or MemoryAdmissionStatus.Updated
            or MemoryAdmissionStatus.AlreadyStored
            or MemoryAdmissionStatus.StoredSessionOnly
            or MemoryAdmissionStatus.UpdatedSessionOnly
            ? MemoryReceipt.Indicator
            : MemoryReceipt.Explicit;
    }

    private static string? Scope(MemoryAdmissionStatus status, MemoryScopeHint? hint) => status switch
    {
        MemoryAdmissionStatus.StoredSessionOnly or MemoryAdmissionStatus.UpdatedSessionOnly => "session",
        MemoryAdmissionStatus.Stored
            or MemoryAdmissionStatus.Updated
            or MemoryAdmissionStatus.AlreadyStored
            or MemoryAdmissionStatus.Deleted => hint switch
            {
                MemoryScopeHint.Session => "session",
                MemoryScopeHint.User => "user",
                _ => "identityUser"
            },
        _ => null
    };

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
