using AgentCore.Application.Decisions;

namespace AgentCore.Infrastructure.Decisions;

// Explicit test-profile backend. No prompt heuristics and no production fallback algorithm.
public sealed class SyntheticDecisionAdapter : IDecisionBackend
{
    public string Id => "synthetic";
    public string Version => "fixture-v1";
    public bool PreAdmissionSupported => true;
    public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) => ValueTask.FromResult(DecisionStatus.Ready);
    public ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var winner = request.Candidates.OrderBy(c => c.Id, StringComparer.Ordinal).First().Id;
        return ValueTask.FromResult(new DecisionResult(DecisionStatus.Ready, winner,
            request.Candidates.ToDictionary(c => c.Id, c => c.Id == winner ? 1d : 0d, StringComparer.Ordinal), null, Id, Version));
    }
}
