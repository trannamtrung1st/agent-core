using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Observability;

public static class RuntimeTelemetry
{
    public const string Name = "AgentCore.Runtime";

    public static ActivitySource Activity { get; } = new(Name);

    public static Meter Meter { get; } = new(Name);

    private static readonly Histogram<double> StageMs = Meter.CreateHistogram<double>("stage_duration_ms");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("dropped_items");

    private static readonly ConcurrentQueue<TimelineEvent> Timeline = new();
    private static readonly ConcurrentDictionary<string, List<double>> Samples = new(StringComparer.Ordinal);
    private static int _timelineLimit = 64;
    private static bool _contentLogging;

    public static void Configure(int timelineCapacity, bool contentLogging)
    {
        _timelineLimit = Math.Clamp(timelineCapacity, 1, 1000);
        _contentLogging = contentLogging;
    }

    public static double ElapsedMs(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public static void Record(string stage, double milliseconds, string? detail = null)
    {
        StageMs.Record(milliseconds, new KeyValuePair<string, object?>("stage", stage));
        Samples.AddOrUpdate(stage, _ => [milliseconds], (_, list) =>
        {
            lock (list)
            {
                list.Add(milliseconds);
            }

            return list;
        });
        var includeDetail = _contentLogging || stage is "initiative_eval";
        Timeline.Enqueue(new TimelineEvent(stage, milliseconds, includeDetail ? detail : null, DateTimeOffset.UtcNow));
        while (Timeline.Count > _timelineLimit)
        {
            Timeline.TryDequeue(out _);
        }
    }

    public static void RecordDropped(string kind)
    {
        Dropped.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public static IReadOnlyList<TimelineEvent> SnapshotTimeline() => [.. Timeline];

    public static IReadOnlyDictionary<string, StageStats> SnapshotStats()
    {
        var result = new Dictionary<string, StageStats>(StringComparer.Ordinal);
        foreach (var pair in Samples)
        {
            lock (pair.Value)
            {
                if (pair.Value.Count == 0)
                {
                    continue;
                }

                var ordered = pair.Value.OrderBy(value => value).ToArray();
                result[pair.Key] = new StageStats(
                    ordered.Length,
                    Percentile(ordered, 0.50),
                    Percentile(ordered, 0.95),
                    ordered[^1]);
            }
        }

        return result;
    }

    public static void Reset()
    {
        Timeline.Clear();
        Samples.Clear();
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}

public sealed record TimelineEvent(string Stage, double DurationMs, string? Detail, DateTimeOffset At);

public sealed record StageStats(int Count, double P50Ms, double P95Ms, double MaxMs);

public static class SafeLogRedactor
{
    private static readonly Regex Secrets = new(
        "api[_-]?key|authorization|bearer|sk-[a-zA-Z0-9]+|additionalheaders",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var query = value.IndexOf('?', StringComparison.Ordinal);
        var withoutQuery = query >= 0 ? value[..query] : value;
        return Secrets.IsMatch(withoutQuery) ? "[redacted]" : withoutQuery;
    }
}

public sealed class ObservabilityOptions
{
    public string LogLevel { get; set; } = "Information";
    public int TimelineCapacity { get; set; } = 64;
    public bool LogConversationContent { get; set; }
    public bool OtlpEnabled { get; set; }
    public string? OtlpEndpoint { get; set; }
}

public sealed class HostingOptions
{
    public string BindUrl { get; set; } = "http://127.0.0.1:5080";
    public string[] AllowedOrigins { get; set; } = ["http://127.0.0.1:5173"];
    public bool UseViteProxy { get; set; } = true;
}
