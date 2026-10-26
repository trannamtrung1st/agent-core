using AgentCore.Domain.Models;

namespace AgentCore.Application.Observability;

// Bounded operational evidence in the incumbent telemetry owner. Never a routing/quality estimate.
public sealed record ModelRoundSummary(int SampleCount, int ErrorCount, DateTimeOffset From, DateTimeOffset To,
    double? P50Milliseconds, double? P95Milliseconds, long OfferingRevision, long ConnectionRevision,
    string Scope = "providerRoundIncludingColdAndRetries");

public static class ModelRoundObservations
{
    private sealed record Sample(DateTimeOffset At, double Milliseconds, bool Error);
    private sealed record Window(ModelRoutePin Route, Queue<Sample> Samples);
    private static readonly object Gate = new();
    private static readonly Dictionary<(Guid Tenant, Guid Offering), Window> Windows = [];
    private const int MaxOfferings = 256, MaxSamples = 64;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    public static void Record(ModelRoutePin route, double milliseconds, bool error, DateTimeOffset at)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || route.TenantId == Guid.Empty || route.OfferingId == Guid.Empty) return;
        lock (Gate)
        {
            foreach (var expiredKey in Windows.Where(pair => pair.Value.Samples.LastOrDefault()?.At < at - Retention).Select(pair => pair.Key).ToArray()) Windows.Remove(expiredKey);
            var key = (route.TenantId, route.OfferingId);
            if (!Windows.TryGetValue(key, out var window) || window.Route != route)
            {
                if (Windows.Count >= MaxOfferings && !Windows.ContainsKey(key)) Windows.Remove(Windows.MinBy(pair => pair.Value.Samples.Last().At).Key);
                Windows[key] = window = new(route, new());
            }
            while (window.Samples.TryPeek(out var oldest) && (oldest.At < at - Retention || window.Samples.Count >= MaxSamples)) window.Samples.Dequeue();
            window.Samples.Enqueue(new(at, milliseconds, error));
        }
    }

    public static ModelRoundSummary? Read(Guid tenant, Guid offering, DateTimeOffset now, ModelRoutePin? expectedRoute = null)
    {
        lock (Gate)
        {
            if (!Windows.TryGetValue((tenant, offering), out var window) || expectedRoute is not null && window.Route != expectedRoute) return null;
            var samples = window.Samples.Where(sample => sample.At >= now - Retention && sample.At <= now).ToArray();
            if (samples.Length == 0) return null;
            var durations = samples.Where(sample => !sample.Error).Select(sample => sample.Milliseconds).Order().ToArray();
            double? Percentile(double fraction) => durations.Length == 0 ? null : durations[Math.Clamp((int)Math.Ceiling(durations.Length * fraction) - 1, 0, durations.Length - 1)];
            return new(samples.Length, samples.Count(sample => sample.Error), samples.Min(sample => sample.At), samples.Max(sample => sample.At),
                Percentile(.5), Percentile(.95), window.Route.OfferingRevision, window.Route.ConnectionRevision);
        }
    }
}
