using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public static class BackgroundCompletionProjection
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string BuildBatch(IReadOnlyList<(AgentRun Run, string Objective)> sources)
    {
        if (sources.Count is < 1 or > 2) throw new ArgumentException("Completion report batch must contain one or two sources.");
        var budget = 800;
        while (true)
        {
            var text = JsonSerializer.Serialize(new { completions = sources.Select(source => new {
                childSessionId = source.Run.SessionId, initialAgentRunId = source.Run.AgentRunId,
                objective = ToolJsonResults.ClipUtf8Prefix(source.Objective, budget / 2), status = source.Run.Status.ToString(),
                summary = ToolJsonResults.ClipUtf8Prefix(source.Run.Result?.Text ?? source.Run.Failure?.Summary ?? "Background work was cancelled.", budget),
                attentionRequired = source.Run.Result?.AttentionRequired == true || source.Run.Status == AgentRunStatus.Failed }) }, Json);
            var evidence = JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(TriggerKind.BackgroundCompleted, text, null), Json);
            if (Encoding.UTF8.GetByteCount(evidence) <= AgentRunLimits.MaxEvidenceBytes) return evidence;
            budget /= 2;
        }
    }

    public static string Build(AgentRun child, string objective, IReadOnlyList<ArtifactRecord> artifacts)
    {
        static string Clip(string value, int bytes) => ToolJsonResults.ClipUtf8Prefix(
            new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()), bytes);
        var task = Clip(objective, 1200);
        var summary = Clip(child.Result?.Text ?? child.Failure?.Summary ?? child.KnownEffectSummary ?? "Background work ended.", 2400);
        var files = artifacts.Where(file => file.SessionId == child.SessionId).Take(3).Select(file => new
        { artifactId = file.ArtifactId, name = Clip(file.DisplayName, 160), contentType = Clip(file.ContentType, 80), file.ByteSize }).ToArray();
        while (true)
        {
            var text = JsonSerializer.Serialize(new
            {
                childSessionId = child.SessionId, initialAgentRunId = child.AgentRunId, objective = task,
                status = child.Status.ToString(), summary,
                attentionRequired = child.Result?.AttentionRequired == true || child.Status == AgentRunStatus.Failed,
                artifacts = files
            }, Json);
            var evidence = JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(TriggerKind.BackgroundCompleted, text, null), Json);
            if (Encoding.UTF8.GetByteCount(evidence) <= AgentRunLimits.MaxEvidenceBytes) return evidence;
            // Account for both JSON layers, including quote/backslash expansion, before admission.
            task = Clip(task, Encoding.UTF8.GetByteCount(task) / 2);
            summary = Clip(summary, Encoding.UTF8.GetByteCount(summary) / 2);
        }
    }
}
