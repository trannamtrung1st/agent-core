using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor(
    IToolConfigurationGate? configurationGate = null,
    IBrowser? browser = null,
    ISandboxService? sandboxService = null,
    ISandboxStore? sandboxStore = null,
    TenantToolAuthorization? tenantAuthorization = null,
    ILogger<SessionToolExecutor>? logger = null)
{
    private readonly IToolConfigurationGate _configurationGate = configurationGate ?? ToolConfigurationGates.Unconfigured;
    internal IToolConfigurationGate ConfigurationGate => _configurationGate;
    internal IReadOnlyList<ModelToolDefinition> ProjectTools(AgentDefinition definition, AgentContext context) =>
        context.ExecutionContext is null ? [] : BrowserNavigateOffer.Apply(ToolCatalog.For(definition, context, _configurationGate), browser);

    internal ValueTask<IReadOnlyList<string>> ContextToolsAsync(AgentRun? run, CancellationToken ct) => run is null || tenantAuthorization is null ? ValueTask.FromResult<IReadOnlyList<string>>([]) : tenantAuthorization.ContextToolsAsync(run, ct);

    public async ValueTask<ResolvedAgentRunConfiguration> ResolveRunConfigurationAsync(SessionSnapshot session, CancellationToken ct, AgentRun? childSource = null)
    {
        if (tenantAuthorization is not null && await tenantAuthorization.ResolveConfigurationAsync(session, ct, childSource).ConfigureAwait(false) is { } tenantConfiguration)
            return tenantConfiguration;
        // Pure runtime fixtures can use their supplied immutable Definition. This reads no private stores
        // and grants no tool authority; production tenant configuration resolves through its owning Run.
        var skills = EffectiveSkillCatalogResolver.Resolve(session.Definition,
            new(session.Definition.SkillList.Select(s => new AgentDefinitionSkillState(session.AgentInstanceId, s.Id, null, 1, session.CreatedAt)).ToArray(), []));
        return new(new(session.Definition, 0, session.PinnedPersonaRevision ?? 0, []),
            session.PinnedPersona ?? session.Definition.Identity, skills, null);
    }

    public async ValueTask<ToolPolicyDecision> EvaluateExecutionPolicyAsync(AgentDefinition definition, Guid sessionId,
        ModelToolCall call, JsonElement args, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (admission?.OwnedSessionId is { } ownedSession && (ownedSession == Guid.Empty || ownedSession != sessionId)) return ToolPolicyDecision.Deny;
        return tenantAuthorization is not null && await tenantAuthorization.PolicyAsync(sessionId, call, admission, ct).ConfigureAwait(false) is { } policy
            ? policy : ToolPolicyDecision.Deny;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(AgentDefinition definition, Guid sessionId, ModelToolCall call,
        int remainingOutputBytes, CancellationToken cancellationToken = default, ToolExecutionAdmission? admission = null)
    {
        using var scope = logger?.BeginScope("Tool={Tool} SessionId={SessionId} AgentInstanceId={AgentInstanceId} AgentRunId={AgentRunId}",
            SafeExecutionTrace.SafeToolName(call.Name), sessionId, admission?.AgentInstanceId, admission?.AgentRunId);
        return await ExecuteCoreAsync(definition, sessionId, call, remainingOutputBytes, cancellationToken, admission).ConfigureAwait(false);
    }

    private async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes,
        CancellationToken cancellationToken = default,
        ToolExecutionAdmission? admission = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tenantAuthorization is not null && call.Name == ToolCatalog.AttachmentsRead
            && await tenantAuthorization.ExecuteAttachmentAsync(sessionId, call, admission, cancellationToken, remainingOutputBytes).ConfigureAwait(false) is { } tenantAttachment)
        {
            var fitted = FitResult(remainingOutputBytes, tenantAttachment.Text);
            return fitted.Text == tenantAttachment.Text ? tenantAttachment : fitted;
        }
        if (tenantAuthorization is not null && await tenantAuthorization.ExecuteAsync(sessionId, call, admission, cancellationToken, remainingOutputBytes).ConfigureAwait(false) is { } tenantResult)
        {
            var result = FitResult(remainingOutputBytes, tenantResult);
            if (TenantWorkspaceMutationContract.IsMutation(call.Name))
            {
                using var json = JsonDocument.Parse(tenantResult);
                var unknown = json.RootElement.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "indeterminate";
                result = result with { OutcomeUnknown = unknown, SafeFailureSummary = unknown ? "Workspace outcome is uncertain. The change was not repeated. Inspect current files before deciding what to do next." : null,
                    EffectObserved = json.RootElement.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.True };
            }
            return result;
        }
        return FitResult(remainingOutputBytes, Error("forbidden", "Trusted tenant execution is required."));
    }

    private static ToolExecutionResult FitResult(int budget, string raw) => ToolExecutionResult.FromText(ToolJsonResults.FitToBudget(budget, raw));
    private static string Error(string code, string message) => JsonSerializer.Serialize(new { error = code, message });
}
