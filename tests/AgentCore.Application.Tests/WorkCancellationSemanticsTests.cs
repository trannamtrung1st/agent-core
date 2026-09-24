using AgentCore.Application.Work;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Tests;

public sealed class WorkCancellationSemanticsTests
{
    [Fact]
    public void InferKnownEffectSummary_distinguishes_uncertain_and_completed_effects()
    {
        var owner = new WorkOwner(Guid.NewGuid(), Guid.NewGuid());
        var provenance = new WorkProvenance(
            Guid.NewGuid(),
            WorkSourceKind.ApplicationEvent,
            null,
            null,
            null,
            "dedupe",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "{}",
            "general-assistant",
            10,
            "Riley");
        var generation = Guid.NewGuid();
        var hash = new string('a', 64);
        const string toolCallId = "tool-call";
        var uncertain = WorkItem.Create(Guid.NewGuid(), owner, provenance, new WorkModelPin("a", "b", "c", "medium"), 3, DateTimeOffset.UtcNow)
            .TakeClaim(generation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1))
            .MarkSideEffect(2, generation, WorkSideEffectDisposition.Prepared, toolCallId, hash, DateTimeOffset.UtcNow)
            .MarkSideEffect(3, generation, WorkSideEffectDisposition.InFlight, toolCallId, hash, DateTimeOffset.UtcNow);
        Assert.Equal(WorkCancellationSemantics.UncertainExternalEffect, WorkCancellationSemantics.InferKnownEffectSummary(uncertain));

        var completed = uncertain
            .MarkSideEffect(4, generation, WorkSideEffectDisposition.Succeeded, toolCallId, hash, DateTimeOffset.UtcNow);
        Assert.Equal(WorkCancellationSemantics.CompletedExternalEffect, WorkCancellationSemantics.InferKnownEffectSummary(completed));
    }
}
