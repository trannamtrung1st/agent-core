using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

/// <summary>Frozen execution identity. Provenance carries no execution permission.</summary>
public enum AgentRunOutputContract { ConversationResponse, BackgroundOutcome, CompletionReport }

public enum AgentRunTaskSource { Automation, AgentRun }
public sealed record AgentRunTaskIntent(string Text, AgentRunTaskSource Source, string? SourceRevision = null);

public sealed class AgentRunAdmission
{
    public AgentRunAdmission(Activation activation, string definitionId, int definitionVersion,
        AgentIdentity pinnedPersona, Guid? responseId, AgentRunOutputContract outputContract, EffectiveExecutionBudget? executionBudget = null, AgentRunConfiguration? configuration = null, ComposerRunInput? composerInput = null, AgentRunTaskIntent? taskIntent = null, TrustedExecutionContext? executionContext = null)
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
            || taskIntent.Text.Length > AgentRunLimits.MaxBackgroundObjectiveCharacters || taskIntent.SourceRevision?.Length > 128))
            throw new ArgumentException("Run task intent is invalid.");
        TaskIntent = taskIntent;
        ExecutionContext = executionContext;
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
