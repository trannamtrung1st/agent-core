using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Observability;

public enum TaskCoordinationEvent { Accepted, Replayed, Refused, Ready, Settled, CancellationRequested, Acknowledged, ReportAdmitted, ReportWithheld }
public enum AgentMessageEvent { Appended, Replayed, WakeAdmitted, WakeWithheld }

/// <summary>Bounded metric labels; identities are trace metadata, never metric dimensions or authored content.</summary>
public static class TaskTelemetry
{
    private static readonly Counter<long> Tasks = RuntimeTelemetry.Meter.CreateCounter<long>("tasks.coordination");
    private static readonly Counter<long> Messages = RuntimeTelemetry.Meter.CreateCounter<long>("agent_messages.delivery");
    private static readonly Histogram<double> Lag = RuntimeTelemetry.Meter.CreateHistogram<double>("cooperation.delivery_lag_ms", "ms");
    private static readonly Histogram<int> RecoveryPage = RuntimeTelemetry.Meter.CreateHistogram<int>("cooperation.recovery_page_size");
    private static readonly Histogram<double> RecoveryAge = RuntimeTelemetry.Meter.CreateHistogram<double>("cooperation.recovery_oldest_age_ms", "ms");

    public static void Task(TaskCoordinationEvent action, DelegatedTask task, string? reason = null)
    {
        Tasks.Add(1, new TagList { { "event", action.ToString() }, { "state", task.State.ToString() }, { "reason", Reason(reason) } });
        Activity.Current?.AddEvent(new("task.coordination", tags: new ActivityTagsCollection {
            { "task.id", task.TaskId }, { "tenant.id", task.Source.TenantId }, { "source.session.id", task.Source.SessionId },
            { "source.run.id", task.Source.RunId }, { "source.instance.id", task.Source.InstanceId },
            { "worker.session.id", task.WorkerSessionId }, { "worker.run.id", task.TerminalRunId ?? task.InitialWorkerRunId },
            { "worker.instance.id", task.Assignment.InstanceId }, { "root.activation.id", task.Source.RootActivationId },
            { "task.revision", task.Revision }, { "event", action.ToString() }, { "reason", Reason(reason) } }));
    }
    public static void Refusal(string reason) => Tasks.Add(1, new TagList { { "event", "Refused" }, { "state", "unadmitted" }, { "reason", Reason(reason) } });
    public static void Message(AgentMessageEvent action, AgentMessage message, AgentMessageReceipt receipt)
    {
        Messages.Add(1, new TagList { { "event", action.ToString() }, { "state", receipt.State.ToString() }, { "reason", Reason(receipt.Reason) } });
        Activity.Current?.AddEvent(new("agent-message.delivery", tags: new ActivityTagsCollection {
            { "tenant.id", message.TenantId }, { "thread.id", message.ThreadId }, { "message.id", message.MessageId },
            { "source.session.id", message.SourceSessionId }, { "source.run.id", message.SourceRunId },
            { "source.instance.id", message.SenderInstanceId }, { "recipient.instance.id", message.RecipientInstanceId },
            { "wake.session.id", receipt.WakeSessionId }, { "wake.run.id", receipt.WakeRunId },
            { "root.activation.id", receipt.WakeRootActivationId }, { "message.sequence", message.Sequence },
            { "receipt.revision", receipt.Revision }, { "event", action.ToString() }, { "reason", Reason(receipt.Reason) } }));
    }
    public static void DeliveryLag(string lane, DateTimeOffset created, DateTimeOffset admitted) => Lag.Record(Math.Max(0, (admitted - created).TotalMilliseconds), new KeyValuePair<string, object?>("lane", Lane(lane)));
    public static void Recovery(string lane, int count, DateTimeOffset? oldest, DateTimeOffset at)
    {
        var tags = new TagList { { "lane", Lane(lane) } };
        RecoveryPage.Record(Math.Clamp(count, 0, 32), tags);
        if (count > 0 && oldest is { } created) RecoveryAge.Record(Math.Max(0, (at - created).TotalMilliseconds), tags);
    }
    private static string Lane(string lane) => lane is "worker" or "dependency" or "message" or "report" ? lane : "other";
    private static string Reason(string? reason) => reason switch {
        null => "none",
        "task_capacity_exceeded" or "TaskCapacityExceeded" or "message_pair_rate_limit" => "capacity",
        "task_root_budget_exhausted" or "task_budget_unavailable" => "budget",
        "message_wake_hop_limit" => "depth",
        "task_deadline_exceeded" => "deadline",
        "task_source_unavailable" or "message_source_unavailable" or "message_source_capability_unavailable" => "source",
        "task_executor_unavailable" or "task_target_unavailable" or "message_recipient_unavailable" => "recipient",
        "task_review_required" or "task_report_review_required" or "message_wake_review_required" => "review",
        "worker_effect_or_outcome_needs_attention" => "uncertain",
        "task_report_parent_unavailable" => "parent",
        _ => "other"
    };
}
