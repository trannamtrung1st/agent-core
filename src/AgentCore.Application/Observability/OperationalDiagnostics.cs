using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentCore.Application.Observability;

public static class OperationalDiagnostics
{
    public const string DetachInstrument = "session.detach";
    public const string AttachInstrument = "session.attach";
    public const string ApprovalInstrument = "approval.outcome";
    public const string ApprovalWaitInstrument = "approval.wait_ms";
    public const string CompactionInstrument = "compaction.outcome";
    public const string MemoryMutationInstrument = "memory.mutation";
    public const string AdminInstrument = "admin.operation";
    public const string AdminTimingInstrument = "admin.operation_ms";
    public const string ToolDenialInstrument = "tool.denial";
    public const string ResourceLimitInstrument = "resource.limit";
    public const string ModelSelectionInstrument = "model.selection";

    private static readonly Counter<long> Detach =
        RuntimeTelemetry.Meter.CreateCounter<long>(DetachInstrument);

    private static readonly Counter<long> Attach =
        RuntimeTelemetry.Meter.CreateCounter<long>(AttachInstrument);

    private static readonly Counter<long> Approval =
        RuntimeTelemetry.Meter.CreateCounter<long>(ApprovalInstrument);

    private static readonly Histogram<double> ApprovalWait =
        RuntimeTelemetry.Meter.CreateHistogram<double>(ApprovalWaitInstrument);

    private static readonly Counter<long> Compaction =
        RuntimeTelemetry.Meter.CreateCounter<long>(CompactionInstrument);

    private static readonly Counter<long> MemoryMutation =
        RuntimeTelemetry.Meter.CreateCounter<long>(MemoryMutationInstrument);

    private static readonly Counter<long> Admin =
        RuntimeTelemetry.Meter.CreateCounter<long>(AdminInstrument);

    private static readonly Histogram<double> AdminTiming =
        RuntimeTelemetry.Meter.CreateHistogram<double>(AdminTimingInstrument);

    private static readonly Counter<long> ToolDenial =
        RuntimeTelemetry.Meter.CreateCounter<long>(ToolDenialInstrument);

    private static readonly Counter<long> ResourceLimit =
        RuntimeTelemetry.Meter.CreateCounter<long>(ResourceLimitInstrument);

    private static readonly Counter<long> ModelSelection =
        RuntimeTelemetry.Meter.CreateCounter<long>(ModelSelectionInstrument);

    private static readonly HashSet<string> DetachPhases = ["ended", "transportOnly", "alreadyPaused"];
    private static readonly HashSet<string> DetachReasons = ["ended", "ending", "transportOnly", "acceptedWork", "alreadyPaused"];
    private static readonly HashSet<string> AttachPhases = ["cold", "resumed", "refused"];
    private static readonly HashSet<string> AttachReasons = ["attached", "deadline", "explicitResume"];
    private static readonly HashSet<string> ApprovalStates = ["waiting", "approved", "rejected", "expired", "superseded", "stale", "idempotent", "unknown"];
    private static readonly HashSet<string> MemoryScopes = ["session", "identityUser", "user"];
    private static readonly HashSet<string> MemoryResults = ["written", "updated", "deleted", "promoted", "reset"];
    private static readonly HashSet<string> AdminOperations =
    [
        "draftCreate",
        "draftFork",
        "draftUpdate",
        "draftDelete",
        "publish",
        "deprecate",
        "instanceCreate",
        "instanceVersion",
        "instancePersona",
        "instanceLifecycle",
        "resolve"
    ];
    private static readonly HashSet<string> AdminOutcomes = ["completed", "rejected", "unchanged", "existing"];
    private static readonly HashSet<string> AdminReasons = ["completed", "validation", "conflict", "notFound", "replay"];
    private static readonly HashSet<string> AdminStates = ["active", "archived", "published", "deprecated", "resolved", "unchanged", "none"];
    private static readonly HashSet<string> ResourceReasons =
    [
        "attachmentItem",
        "attachmentSession",
        "workspaceContentLength",
        "workspaceStream",
        "workspaceStore",
        "artifactItem",
        "artifactSession"
    ];

    public static void RecordDetach(string phase, string reason)
    {
        var safePhase = Allow(DetachPhases, phase);
        var safeReason = Allow(DetachReasons, reason);
        Detach.Add(1, new TagList { { "phase", safePhase }, { "reason", safeReason } });
        RuntimeTelemetry.RecordDiagnostic(DetachInstrument, 0, $"{safePhase}:{safeReason}");
    }

    public static void RecordAttach(string phase, string reason)
    {
        var safePhase = Allow(AttachPhases, phase);
        var safeReason = Allow(AttachReasons, reason);
        Attach.Add(1, new TagList { { "phase", safePhase }, { "reason", safeReason } });
        RuntimeTelemetry.RecordDiagnostic(AttachInstrument, 0, $"{safePhase}:{safeReason}");
    }

    public static void RecordApproval(string state, string reason, double? elapsedMs)
    {
        var safeState = Allow(ApprovalStates, state);
        var safeReason = Allow(ApprovalStates, reason);
        Approval.Add(1, new TagList { { "state", safeState }, { "reason", safeReason } });
        if (elapsedMs is { } milliseconds)
        {
            ApprovalWait.Record(Math.Max(0, milliseconds), new TagList { { "state", safeState } });
        }

        RuntimeTelemetry.RecordDiagnostic(ApprovalInstrument, elapsedMs ?? 0, $"{safeState}:{safeReason}");
    }

    public static void RecordCompactionAccepted()
    {
        Compaction.Add(1, new TagList { { "outcome", "accepted" } });
        RuntimeTelemetry.RecordDiagnostic(CompactionInstrument, 0, "accepted");
    }

    public static void RecordMemoryMutation(string scope, string result)
    {
        var safeScope = Allow(MemoryScopes, scope);
        var safeResult = Allow(MemoryResults, result);
        MemoryMutation.Add(1, new TagList { { "scope", safeScope }, { "result", safeResult } });
        RuntimeTelemetry.RecordDiagnostic(MemoryMutationInstrument, 0, $"{safeScope}:{safeResult}");
    }

    public static void RecordAdmin(
        string operation,
        string outcome,
        string reason,
        long startedTimestamp,
        string? definitionId,
        int? version,
        Guid? instanceId,
        string? state)
    {
        var safeOperation = Allow(AdminOperations, operation);
        var safeOutcome = Allow(AdminOutcomes, outcome);
        var safeReason = Allow(AdminReasons, reason);
        var safeState = Allow(AdminStates, state ?? "none");
        var elapsed = RuntimeTelemetry.ElapsedMs(startedTimestamp);
        Admin.Add(1, new TagList
        {
            { "operation", safeOperation },
            { "outcome", safeOutcome },
            { "reason", safeReason }
        });
        AdminTiming.Record(elapsed, new TagList { { "operation", safeOperation } });
        var detail = SafeLogRedactor.Redact(
            $"definitionId={definitionId};version={version?.ToString() ?? ""};instanceId={(instanceId is { } id ? id.ToString("D") : "")};state={safeState}");
        RuntimeTelemetry.RecordDiagnostic(AdminInstrument, elapsed, detail);
    }

    public static void RecordToolDenial(string? toolName)
    {
        ToolDenial.Add(1, new TagList { { "reason", "forbidden" } });
        var name = string.IsNullOrWhiteSpace(toolName) ? "unnamed" : toolName.Trim();
        if (name.Length > 80)
        {
            name = name[..80];
        }

        RuntimeTelemetry.RecordDiagnostic(ToolDenialInstrument, 0, SafeLogRedactor.Redact(name));
    }

    public static void RecordResourceLimit(string reason)
    {
        var safeReason = Allow(ResourceReasons, reason);
        ResourceLimit.Add(1, new TagList { { "reason", safeReason } });
        RuntimeTelemetry.RecordDiagnostic(ResourceLimitInstrument, 0, safeReason);
    }

    public static void RecordModelSelection(string? catalogKey, string? providerAlias)
    {
        ModelSelection.Add(1, new TagList { { "result", "selected" } });
        var key = string.IsNullOrWhiteSpace(catalogKey) ? "unspecified" : catalogKey.Trim();
        var alias = string.IsNullOrWhiteSpace(providerAlias) ? "unspecified" : providerAlias.Trim();
        var detail = SafeLogRedactor.Redact($"catalogKey={key};providerAlias={alias}");
        RuntimeTelemetry.RecordDiagnostic(ModelSelectionInstrument, 0, detail);
    }

    private static string Allow(HashSet<string> allowed, string? value) =>
        value is not null && allowed.Contains(value) ? value : "other";
}
