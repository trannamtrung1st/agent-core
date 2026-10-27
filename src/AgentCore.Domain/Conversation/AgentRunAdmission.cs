using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

/// <summary>Frozen execution identity. Provenance carries no execution permission.</summary>
public enum AgentRunOutputContract { ConversationResponse, BackgroundOutcome, CompletionReport }

public enum AgentRunTaskSource { Automation, AgentRun, DelegatedTask, AgentMessage }
public sealed record DelegatedTaskWork(Guid TaskId, long TaskRevision, string AssignmentHash);
public sealed record TaskReportWork(Guid TaskId, long TaskRevision, Guid NotificationId, Guid TerminalRunId);
public sealed record AgentMessageWork(Guid MessageId, Guid ThreadId, Guid WakeRootActivationId, int WakeHops);
public sealed record AgentRunTaskIntent(string Text, AgentRunTaskSource Source, string? SourceRevision = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AgentCore.Domain.Triggers.TriggerSourceKind? EventSourceKind = null);
public sealed record TenantApprovalResume(Guid SourceRunId, Guid RequestId, Guid OperationId, string ActionHash, IReadOnlyList<Guid> SourceInputEntryIds);

public sealed class AgentRunAdmission
{
    public AgentRunAdmission(Activation activation, string definitionId, int definitionVersion,
        AgentIdentity pinnedPersona, Guid? responseId, AgentRunOutputContract outputContract, EffectiveExecutionBudget? executionBudget = null, AgentRunConfiguration? configuration = null, ComposerRunInput? composerInput = null, AgentRunTaskIntent? taskIntent = null, TrustedExecutionContext? executionContext = null, TenantApprovalResume? approvalResume = null, AgentCore.Domain.Goals.GoalWorkContext? goalWork = null, DelegatedTaskWork? delegatedTask = null, TaskReportWork? taskReport = null, AgentMessageWork? agentMessage = null)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(pinnedPersona);
        if (definitionVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(definitionVersion));
        AgentRunText.RequireOptionalId(responseId, "Response");
        if (activation.Kind == ActivationKind.UserTurn && responseId is null)
            throw new ArgumentException("A user turn requires stable response identity.");
        if (!Enum.IsDefined(outputContract)) throw new ArgumentException("Output contract is invalid.");
        executionBudget?.Profile.Validate();
        if (executionBudget is not null && (!Enum.IsDefined(executionBudget.Class) || executionBudget.Source is not ("instance" or "definition" or "system")))
            throw new ArgumentException("Execution budget class/source is invalid.");
        if (configuration is not null && (configuration.Definition.Id != definitionId || configuration.Definition.Version != definitionVersion))
            throw new ArgumentException("Run configuration disagrees with admission identity.");
        ComposerInput = composerInput is null ? null : composerInput with {
            ExplicitSkillKeys = Array.AsReadOnly(composerInput.ExplicitSkillKeys.ToArray()),
            References = Array.AsReadOnly(composerInput.References.ToArray()) };
        if (taskIntent is not null && (!Enum.IsDefined(taskIntent.Source) || string.IsNullOrWhiteSpace(taskIntent.Text)
            || taskIntent.Text.Length > AgentRunLimits.MaxBackgroundObjectiveCharacters || taskIntent.SourceRevision?.Length > 128
            || taskIntent.EventSourceKind is not null && (taskIntent.Source != AgentRunTaskSource.Automation
                || taskIntent.EventSourceKind is not (AgentCore.Domain.Triggers.TriggerSourceKind.CoreEvent or AgentCore.Domain.Triggers.TriggerSourceKind.ApplicationEvent))))
            throw new ArgumentException("Run task intent is invalid.");
        if (delegatedTask is not null && (delegatedTask.TaskId == Guid.Empty || delegatedTask.TaskRevision < 1
            || delegatedTask.AssignmentHash.Length != 64 || taskIntent?.Source != AgentRunTaskSource.DelegatedTask
            || taskIntent.SourceRevision != delegatedTask.TaskId.ToString("D"))) throw new ArgumentException("Task Run requires its exact immutable assignment witness.");
        if (activation.Kind == ActivationKind.DelegatedTask && delegatedTask is null) throw new ArgumentException("Delegated activation requires a task witness.");
        DelegatedTask = delegatedTask;
        if (taskReport is not null && (taskReport.TaskId == Guid.Empty || taskReport.TaskRevision < 1 || taskReport.NotificationId == Guid.Empty
            || taskReport.TerminalRunId == Guid.Empty || outputContract != AgentRunOutputContract.CompletionReport
            || activation.Kind is not (ActivationKind.TaskCompletion or ActivationKind.ApprovalContinuation)
            || delegatedTask is not null)) throw new ArgumentException("Task report requires its exact saved notice and terminal Run.");
        if (activation.Kind == ActivationKind.TaskCompletion && taskReport is null) throw new ArgumentException("Task completion requires a notice witness.");
        TaskReport = taskReport;
        if (agentMessage is not null && (agentMessage.MessageId == Guid.Empty || agentMessage.ThreadId == Guid.Empty || agentMessage.WakeRootActivationId == Guid.Empty
            || agentMessage.WakeHops is < 1 or > 2 || outputContract != AgentRunOutputContract.BackgroundOutcome || delegatedTask is not null || taskReport is not null
            || taskIntent?.Source != AgentRunTaskSource.AgentMessage || taskIntent.SourceRevision != agentMessage.MessageId.ToString("D")
            || activation.Kind is not (ActivationKind.AgentMessage or ActivationKind.ApprovalContinuation))) throw new ArgumentException("Message wake requires its exact bounded message witness.");
        if (activation.Kind == ActivationKind.AgentMessage && agentMessage is null) throw new ArgumentException("Message wake requires a message witness.");
        AgentMessage = agentMessage;
        TaskIntent = taskIntent;
        ExecutionContext = executionContext;
        if ((activation.Kind == ActivationKind.ApprovalContinuation) != (approvalResume is not null)
            || approvalResume is not null && (new[] { approvalResume.SourceRunId, approvalResume.RequestId, approvalResume.OperationId }.Any(id => id == Guid.Empty)
                || approvalResume.SourceRunId != activation.SourceAgentRunId || executionContext?.Origin != ExecutionOrigin.ApprovalContinuation
                || approvalResume.ActionHash.Length != AgentRunLimits.ActionHashCharacters || responseId is null
                || (approvalResume.SourceInputEntryIds.Count == 0 && outputContract != AgentRunOutputContract.CompletionReport && taskIntent?.Source != AgentRunTaskSource.Automation
                    && !(outputContract == AgentRunOutputContract.ConversationResponse && executionContext?.OriginalInitiatorPrincipalId is not null && HasConfigurationBoundary(activation)) || approvalResume.SourceInputEntryIds.Count > Activation.MaxSourceEntries)
                || approvalResume.SourceInputEntryIds.Any(id => id == Guid.Empty) || approvalResume.SourceInputEntryIds.Distinct().Count() != approvalResume.SourceInputEntryIds.Count))
            throw new ArgumentException("Approval continuation requires its exact trusted operation and original input references.");
        if (goalWork is not null && (new[] { goalWork.GoalId, goalWork.CheckpointId, goalWork.SessionId, goalWork.SpaceId }.Any(id => id == Guid.Empty)
            || goalWork.SessionId != activation.SessionId || goalWork.PlanRevision < 1 || goalWork.GoalRevision < 1 || goalWork.FocusRevision < 1
            || goalWork.PlanHash.Length != 64 || goalWork.CheckpointHash.Length != 64 || goalWork.InputSequence < 1 || goalWork.TaskJson.Length > 16000
            || activation.Kind is not (ActivationKind.UserTurn or ActivationKind.GoalContinuation or ActivationKind.ApprovalContinuation)))
            throw new ArgumentException("Goal work requires a bounded exact admission witness.");
        GoalWork = goalWork;
        ApprovalResume = approvalResume is null ? null : approvalResume with { SourceInputEntryIds = Array.AsReadOnly(approvalResume.SourceInputEntryIds.ToArray()) };
        Configuration = configuration;
        ExecutionBudget = executionBudget;
        OutputContract = outputContract;
        Activation = activation;
        DefinitionId = AgentRunText.RequireToken(definitionId, AgentRunLimits.MaxDefinitionIdCharacters, "Definition");
        DefinitionVersion = definitionVersion;
        PinnedPersona = pinnedPersona;
        ResponseId = responseId;
    }

    // Structural allowance for an input-free bounded continuation. Current source/receipt authority is checked by its owning transaction.
    private static bool HasConfigurationBoundary(Activation activation)
    {
        if (activation.EvidenceJson is not { } evidence) return false;
        using var document = System.Text.Json.JsonDocument.Parse(evidence); var value = document.RootElement;
        return value.TryGetProperty("EnvironmentKind", out var kind) && kind.ValueKind == System.Text.Json.JsonValueKind.String && (kind.GetString() == "configuration"
                && value.TryGetProperty("ContinuationTarget", out var target) && target.ValueKind == System.Text.Json.JsonValueKind.String && target.GetString() is { Length: > 0 and <= 64 }
                || (kind.GetString() == "goal-continuation" || kind.GetString() == "context-transition") && (kind.GetString() == "goal-continuation" || value.TryGetProperty("ContextTransition", out var transition) && transition.ValueKind == System.Text.Json.JsonValueKind.Object))
            && value.TryGetProperty("InputBoundarySequence", out var boundary) && boundary.ValueKind == System.Text.Json.JsonValueKind.Number && boundary.TryGetInt64(out var sequence) && sequence >= 0;
    }
    public AgentCore.Domain.Goals.GoalWorkContext? GoalWork { get; }
    public AgentRunTaskIntent? TaskIntent { get; }
    public TenantApprovalResume? ApprovalResume { get; }
    public DelegatedTaskWork? DelegatedTask { get; }
    public TaskReportWork? TaskReport { get; }
    public AgentMessageWork? AgentMessage { get; }
    public TrustedExecutionContext? ExecutionContext { get; }
    public ComposerRunInput? ComposerInput { get; }
    public AgentRunConfiguration? Configuration { get; }
    public EffectiveExecutionBudget? ExecutionBudget { get; }
    public AgentRunOutputContract OutputContract { get; }
    public Activation Activation { get; }
    public string DefinitionId { get; }
    public int DefinitionVersion { get; }
    public AgentIdentity PinnedPersona { get; }
    public Guid? ResponseId { get; }
}
