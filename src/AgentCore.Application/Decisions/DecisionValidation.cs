using System.Collections.ObjectModel;

namespace AgentCore.Application.Decisions;

public static class DecisionValidation
{
    public const int MaxCandidates = 32;
    public static bool ValidRequest(DecisionRequest request, int maxCharacters)
    {
        if (!Enum.IsDefined(request.Purpose) || !Enum.IsDefined(request.Kind) || request.Scope is null
            || request.Candidates is null || request.Signals is null || request.PolicyRevision is not { Length: > 0 and <= 128 }
            || !double.IsFinite(request.MinimumProbability) || request.MinimumProbability is < 0.5 or > 1
            || request.Candidates.Count is < 1 or > MaxCandidates || request.Signals.Count > 16) return false;
        if (request.Kind == DecisionKind.YesNo && !request.Candidates.Select(c => c.Id).SequenceEqual(new[] { "false", "true" })) return false;
        if (request.Kind == DecisionKind.Score && !request.Candidates.Select(c => c.Id).SequenceEqual(Enumerable.Range(0, request.Candidates.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)))) return false;
        if (request.Candidates.Any(c => c.Id is not { Length: > 0 and <= 128 } || c.Id.Any(char.IsControl)
            || c.Label is not { Length: > 0 and <= 256 } || c.Description?.Length > 512)
            || request.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count
            || request.Signals.Any(s => s.Key.Length is < 1 or > 64 || s.Value is null)) return false;
        return request.Candidates.Sum(c => (long)c.Id.Length + c.Label.Length + (c.Description?.Length ?? 0))
            + request.Signals.Sum(s => (long)s.Key.Length + s.Value.Length) <= maxCharacters;
    }

    public static DecisionResult Validate(DecisionRequest request, DecisionResult result, string engine, string version)
    {
        DecisionResult Invalid() => DecisionResult.Failure(DecisionStatus.InvalidResult, engine, version);
        if (result.EngineId != engine || result.EngineVersion != version || !Enum.IsDefined(result.Status)) return Invalid();
        if (result.Status != DecisionStatus.Ready) return DecisionResult.Failure(result.Status, engine, version);
        var p = result.Probabilities;
        if (p is null || p.Count != request.Candidates.Count || request.Candidates.Any(c => !p.ContainsKey(c.Id))
            || p.Values.Any(v => !double.IsFinite(v) || v is < 0 or > 1) || Math.Abs(p.Values.Sum() - 1) > 0.002) return Invalid();
        // Stable caller order breaks ties. Neither the backend's choice nor its confidence is trusted.
        var winner = request.Candidates.OrderByDescending(c => p[c.Id]).First().Id;
        if (result.SelectedCandidateId is not null && result.SelectedCandidateId != winner) return Invalid();
        var score = request.Kind == DecisionKind.Score ? request.Candidates.Select((c, i) => p[c.Id] * i).Sum() : (double?)null;
        if (result.Score is { } supplied && (!double.IsFinite(supplied) || score is null || Math.Abs(supplied - score.Value) > 0.003)) return Invalid();
        if (p[winner] < request.MinimumProbability) return DecisionResult.Failure(DecisionStatus.LowConfidence, engine, version);
        return result with { SelectedCandidateId = winner, Score = score,
            Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(p, StringComparer.Ordinal)), DiagnosticCode = null };
    }
}
