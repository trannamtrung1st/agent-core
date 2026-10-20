using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

/// <summary>Frozen execution identity. Provenance carries no execution permission.</summary>
public enum AgentRunOutputContract { ConversationResponse, BackgroundOutcome, CompletionReport }

public enum AgentRunTaskSource { Automation, AgentRun }
public sealed record AgentRunTaskIntent(string Text, AgentRunTaskSource Source, string? SourceRevision = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AgentCore.Domain.Triggers.TriggerSourceKind? EventSourceKind = null);
public sealed record TenantApprovalResume(Guid SourceRunId, Guid RequestId, Guid OperationId, string ActionHash, IReadOnlyList<Guid> SourceInputEntryIds);

public sealed class AgentRunAdmission
{
    public AgentRunAdmission(Activation activation, string definitionId, int definitionVersion,
        AgentIdentity pinnedPersona, Guid? responseId, AgentRunOutputContract outputContract, EffectiveExecutionBudget? executionBudget = null, AgentRunConfiguration? configuration = null, ComposerRunInput? composerInput = null, AgentRunTaskIntent? taskIntent = null, TrustedExecutionContext? executionContext = null, TenantApprovalResume? approvalResume = null)
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
        TaskIntent = taskIntent;
        ExecutionContext = executionContext;
        if ((activation.Kind == ActivationKind.ApprovalContinuation) != (approvalResume is not null)
            || approvalResume is not null && (new[] { approvalResume.SourceRunId, approvalResume.RequestId, approvalResume.OperationId }.Any(id => id == Guid.Empty)
                || approvalResume.SourceRunId != activation.SourceAgentRunId || executionContext?.Origin != ExecutionOrigin.ApprovalContinuation
                || approvalResume.ActionHash.Length != AgentRunLimits.ActionHashCharacters || responseId is null
                || (approvalResume.SourceInputEntryIds.Count == 0 && outputContract != AgentRunOutputContract.CompletionReport || approvalResume.SourceInputEntryIds.Count > Activation.MaxSourceEntries)
                || approvalResume.SourceInputEntryIds.Any(id => id == Guid.Empty) || approvalResume.SourceInputEntryIds.Distinct().Count() != approvalResume.SourceInputEntryIds.Count))
            throw new ArgumentException("Approval continuation requires its exact trusted operation and original input references.");
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

    public AgentRunTaskIntent? TaskIntent { get; }
    public TenantApprovalResume? ApprovalResume { get; }
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
