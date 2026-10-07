using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

// Historical candidate recovery is tested through Authoring owners; no second model runtime.
internal static class HarnessLegacyFixture
{
    public static async ValueTask<HarnessReview> RunAsync(IServiceProvider services, Guid id, Guid prepId)
    {
        var service = services.GetRequiredService<HarnessManagementService>();
        var review = await service.ReviewAsync(id);
        var draft = review.Draft!;
        if (review.State.Policy.Allows(HarnessManagementScope.KnowledgeResources)
            && !draft.Candidate.Environment!.KnowledgeList.Any(k => k.Identity == "preparation-reference"))
        {
            await service.RequestOperationAsync(id, prepId, new("knowledge.upsert", draft.Revision, Id: "preparation-reference",
                Content: "Review permitted order policy and stop before production changes.", Source: "knowledge:support-order-policy"));
            review = await service.ReviewAsync(id); draft = review.Draft!;
        }
        if (review.State.Policy.Allows(HarnessManagementScope.ToolSelection) && review.State.Preparation!.Approvals.Count == 0)
        {
            await service.RequestOperationAsync(id, prepId, new("tool.select", draft.Revision, Id: "web.fetch", Enabled: true));
            return await service.ReviewAsync(id);
        }
        await service.RecordAgentEvidenceAsync(id, prepId, new("Agent", draft.Revision, "Historical candidate assessment", HarnessEvidenceStatus.PartiallyVerified,
            "Inspect candidate safely.", "Candidate inspected.", "Production outcomes remain external evidence."));
        await service.VerifyAsync(id, prepId);
        return await service.ReviewAsync(id);
    }
}
