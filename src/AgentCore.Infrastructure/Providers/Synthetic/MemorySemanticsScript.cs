using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Memory;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Deterministic close/forget regression through the ordinary response envelope.</summary>
internal static class MemorySemanticsScript
{
    internal const string Subject = "Vietnam car prices research";

    internal static bool TryRespond(ModelRequest request, string userText,
        out IReadOnlyList<string> chunks, out IReadOnlyList<MemoryProposal> proposals)
    {
        chunks = [];
        proposals = [];
        MemoryProposal Proposal(MemoryProposalOperation operation, string subject = Subject) =>
            new(operation, MemoryKind.OpenLoop, subject,
                operation == MemoryProposalOperation.Upsert ? "Research report at /home/car-prices.md awaits review." : "",
                null, operation == MemoryProposalOperation.Upsert ? MemoryProposalSource.AgentInferred : MemoryProposalSource.UserExplicit);
        switch (userText)
        {
            case "synthetic-memory-semantics: start-research":
                chunks = ["The research is pending review."];
                proposals = [Proposal(MemoryProposalOperation.Upsert)];
                return true;
            case "Close the Vietnam car prices research work; keep the report.":
                chunks = ["I will stop treating the research as pending work and keep the report."];
                proposals = [Proposal(MemoryProposalOperation.Resolve)];
                return true;
            case "synthetic-memory-semantics: propose-delete":
                chunks = ["Understood."];
                proposals = [Proposal(MemoryProposalOperation.Delete)];
                return true;
            case "synthetic-memory-semantics: close-missing":
                chunks = ["I checked the requested work."];
                proposals = [Proposal(MemoryProposalOperation.Resolve, "Missing research")];
                return true;
            case "synthetic-memory-semantics: explain-receipt":
                var rejected = request.Messages.Any(message => message.Role == ModelRole.System
                    && message.Text.Contains("Core memory admission outcomes") && message.Text.Contains("approvalRequired"));
                chunks = [rejected ? "Core rejected a delete attempt; no memory was forgotten."
                    : "No recorded deletion outcome is available."];
                return true;
            default:
                return false;
        }
    }
}
