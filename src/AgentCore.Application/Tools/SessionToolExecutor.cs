using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Agents;
using AgentCore.Application.Admin;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor(
    RoleKnowledgeService? knowledge = null,
    IAttachmentStore? attachments = null,
    IAttachmentProcessor? processor = null,
    ISessionWorkspace? workspace = null,
    IArtifactStore? artifacts = null,
    ISandboxExecutor? sandbox = null,
    IWebSearchProvider? webSearch = null,
    IPublicWebFetcher? publicWebFetcher = null,
    IEmailProvider? emailProvider = null,
    IHttpRequestClient? httpRequestClient = null,
    IToolConfigurationGate? configurationGate = null,
    IAutomationService? triggerRegistrations = null,
    ITriggerCommandAuthorizer? triggerAuthorizer = null,
    IAgentInstanceStore? agentInstances = null,
    IAgentDefinitionStore? agentDefinitions = null,
    IMemoryStore? profiles = null,
    IBrowser? browser = null,
    IAgentDefinitionResourceAdminStore? definitionResources = null,
    Func<HarnessManagementService>? harnessAuthoring = null,
    AgentCore.Application.Experience.ExperienceService? experience = null,
    AgentCore.Application.Continuity.ContinuityService? continuity = null,
    AgentCore.Application.Continuity.IdentityMaintenanceService? identityMaintenance = null,
    AgentCore.Application.Workspaces.AgentInstanceWorkspaceService? agentWorkspace = null,
    AgentCore.Application.Credentials.CredentialService? credentials = null,
    AdminAutomationAuthoringService? automationAuthoring = null, AgentInstanceSkillService? instanceSkills = null)
{
    private readonly IAgentInstanceStore? _agentInstances = agentInstances;
    private readonly IAgentDefinitionStore? _agentDefinitions = agentDefinitions;
    private readonly IMemoryStore? _profiles = profiles;
    private readonly IToolConfigurationGate _configurationGate =
        configurationGate ?? ToolConfigurationGates.Unconfigured;

    internal IToolConfigurationGate ConfigurationGate => _configurationGate;
    internal IReadOnlyList<ModelToolDefinition> ProjectTools(AgentDefinition definition, AgentContext context) =>
        BrowserNavigateOffer.Apply(ToolCatalog.For(definition, context, _configurationGate), browser);

    private readonly ITriggerCommandAuthorizer _triggerAuthorizer =
        triggerAuthorizer ?? new HeuristicTriggerCommandAuthorizer();

    public ITriggerCommandAuthorizer TriggerCommandAuthorizer => _triggerAuthorizer;

    public async ValueTask<bool> CredentialMetadataAvailableAsync(Guid? id, CancellationToken ct) =>
        id is Guid owner && credentials is not null && (await credentials.SafeMetadataPageAsync(owner, null, 1, ct)).Count > 0;

    public ValueTask<string> ExperienceContextAsync(Guid? instanceId, CancellationToken ct) =>
        experience?.RecallAsync(instanceId, ct) ?? ValueTask.FromResult("");
    public async ValueTask<IReadOnlyList<ArtifactRecord>> CompletionArtifactsAsync(Guid sessionId, CancellationToken ct) =>
        artifacts is null ? [] : (await artifacts.ListPageAsync(sessionId, null, 3, ct).ConfigureAwait(false)).Items;

    public ValueTask<List<ModelMessage>> RehydrateCapturesAsync(Guid sessionId, List<ModelMessage> messages, bool supportsVision, CancellationToken ct) =>
        supportsVision ? AgentCore.Application.Execution.SessionCaptureRehydration.ApplyAsync(sessionId, messages, artifacts, ct)
            : ValueTask.FromResult(messages);

    public ValueTask<string> SelectedExperienceContextAsync(Guid instanceId, Guid workId, CancellationToken ct) =>
        experience?.SelectedSourceAsync(instanceId, workId, ct) ?? ValueTask.FromResult("");
    public ValueTask<string> ContinuityContextAsync(Guid? instanceId, string? query, Guid? sessionId, AgentDefinition definition, CancellationToken ct) =>
        continuity?.ContextAsync(instanceId, query, sessionId, definition, ct) ?? ValueTask.FromResult("");


    public async ValueTask<HarnessChatContext?> HarnessContextAsync(Guid? instanceId, CancellationToken ct)
    {
        if (instanceId is not Guid id || _agentInstances is null || harnessAuthoring is null) return null;
        var instance = await _agentInstances.FindAsync(id, ct);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active
            || instance.HarnessManagement is not { } state || state.Policy.Frozen || state.Policy.Mode == HarnessManagementMode.Disabled) return null;
        return new(state.Policy, state.PolicyRevision, instance.ActiveVersion);
    }

    public ValueTask<IReadOnlyList<EffectiveSkill>> ResolveSkillCatalogAsync(Guid? owner, AgentDefinition definition, CancellationToken ct) =>
        owner is Guid id && _agentInstances is not null ? new EffectiveSkillCatalogResolver(_agentInstances).ResolveAsync(id, definition, ct)
        : definition.SkillList.Count == 0 ? ValueTask.FromResult<IReadOnlyList<EffectiveSkill>>([])
        : throw AgentCoreErrors.Persistence("Skill resolution requires a trusted Agent Instance store.");

    public ToolPolicyDecision EvaluateExecutionPolicy(
        AgentDefinition definition,
        string toolName,
        ToolApprovalGrant? grant = null,
        ToolExecutionAdmission? admission = null) =>
        ToolPolicy.EvaluateExecution(definition, toolName, _configurationGate, grant, admission);
    public ValueTask<bool> AllowsAgentConsolidationAsync(Guid? id, CancellationToken ct) =>
        identityMaintenance?.AllowsAutonomousAsync(id, ct) ?? ValueTask.FromResult(false);

    public async ValueTask<ToolPolicyDecision> EvaluateExecutionPolicyAsync(AgentDefinition definition, Guid sessionId,
        ModelToolCall call, JsonElement args, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (admission?.OwnedSessionId is { } ownedSession && (ownedSession == Guid.Empty || ownedSession != sessionId)) return ToolPolicyDecision.Deny;
        if (!ToolCatalog.IsIdentityMaintenance(call.Name)) return EvaluateExecutionPolicy(definition, call.Name, admission: admission);
        if (identityMaintenance is null) { RecordMaintenanceRejection(call.Name); return ToolPolicyDecision.Deny; }
        try
        {
            var policy = await identityMaintenance.PolicyAsync(definition, sessionId, call.Name, args, admission, ct);
            if (policy == ToolPolicyDecision.Deny) RecordMaintenanceRejection(call.Name);
            return policy;
        }
        // Malformed arguments are handled by the semantic executor before any mutation.
        // Preserve its actionable validation result so the model can repair the call.
        catch (AgentCoreException ex) when (ex.StatusCode == 400) { return ToolPolicyDecision.Allow; }
        catch (AgentCoreException) { RecordMaintenanceRejection(call.Name); return ToolPolicyDecision.Deny; }
    }

    private static void RecordMaintenanceRejection(string tool) => RuntimeTelemetry.RecordIdentityMaintenance(
        tool == ToolCatalog.MemoryForget ? "forget_rejected" : "rejected_by_policy");

    public async ValueTask<(ToolApprovalPreparation? Preparation, string? ErrorJson)> PrepareIdentityMaintenanceApprovalAsync(
        AgentDefinition? definition, Guid sessionId, ModelToolCall call, JsonElement args, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (identityMaintenance is null || definition is null || admission is null)
            return (null, Error("forbidden", "Trusted identity maintenance context is required."));
        try
        {
            var preview = await identityMaintenance.ApprovalPreviewAsync(definition, sessionId, call.Name, args, admission, ct);
            return (new(ToolActionHash.Compute(call.Name, args), preview.Summary, call.ArgumentsJson, preview.Details), null);
        }
        catch (AgentCoreException ex) { return (null, Error(ex.Code, ex.Message)); }
        catch (ArgumentException) { return (null, Error("invalid", "Maintenance approval exceeds its content bound.")); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes,
        CancellationToken cancellationToken = default,
        ToolApprovalGrant? approvalGrant = null,
        TriggerCommandContext? triggerCommand = null,
        ToolExecutionAdmission? admission = null)
    {
        var result = await ExecuteCoreAsync(definition, sessionId, call, remainingOutputBytes,
            cancellationToken, approvalGrant, triggerCommand, admission).ConfigureAwait(false);
        // Discovery failures from admission/parsing must respect the budget too.
        return call.Name == ToolCatalog.CredentialsList ? FitResult(remainingOutputBytes, result.Text) : result;
    }

    private async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes,
        CancellationToken cancellationToken = default,
        ToolApprovalGrant? approvalGrant = null,
        TriggerCommandContext? triggerCommand = null,
        ToolExecutionAdmission? admission = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(call.Name, ToolCatalog.SkillsLoad, StringComparison.Ordinal)
            || string.Equals(call.Name, ToolCatalog.AppMessageSend, StringComparison.Ordinal))
        {
            return TextResult(Error("forbidden", "Tool effect is owned by the session runtime."));
        }

        if (string.Equals(call.Name, ToolCatalog.WorkComplete, StringComparison.Ordinal))
        {
            return TextResult(Error("forbidden", "Completion is owned by the occurrence."));
        }

        if (admission?.OwnedSessionId is { } ownedSession && (ownedSession == Guid.Empty || ownedSession != sessionId))
            return TextResult(Error("forbidden", "Session resource authority does not match."));
        if (admission?.Detached == true
            && admission.OwnedSessionId != sessionId
            && ToolResources.IsSessionTool(call.Name)
            && !(HarnessChatTools.IsHarness(call.Name) && ToolResources.IsOccurrence(admission.TriggerKind))
            && !(ToolCatalog.IsBrowserTool(call.Name)
                && ToolResources.IsOccurrence(admission.TriggerKind)
                && admission.AgentInstanceId is Guid agentInstanceId
                && agentInstanceId != Guid.Empty))
        {
            return TextResult(Error("forbidden", "Session context is required."));
        }

        if (admission is not null
            && ToolResources.IsOccurrence(admission.TriggerKind)
            && ToolResources.IsTriggerWrite(call.Name))
        {
            return TextResult(Error("forbidden", "Trigger changes are not authorized from occurrence evidence."));
        }

        if (HarnessChatTools.IsHarness(call.Name) && admission is not null)
            admission = admission with { Harness = await HarnessContextAsync(admission.AgentInstanceId, cancellationToken) };

        var policy = ToolPolicy.EvaluateExecution(definition, call.Name, _configurationGate, approvalGrant, admission);
        if (policy == ToolPolicyDecision.Deny
            || string.IsNullOrWhiteSpace(call.Name))
        {
            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("forbidden", "Tool is not permitted for this role."));
        }

        if (policy == ToolPolicyDecision.RequireApproval)
        {
            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("approval_required", "Tool execution requires explicit approval."));
        }

        JsonElement args;
        try
        {
            if (string.Equals(call.Name, ToolCatalog.BrowserClose, StringComparison.Ordinal))
            {
                if (!BrowserToolArguments.TryValidateClose(call.ArgumentsJson, out var closeError))
                {
                    return TextResult(closeError);
                }

                args = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson, JsonOptions);
            }
            else
            {
                args = JsonSerializer.Deserialize<JsonElement>(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                    JsonOptions);
                if (args.ValueKind != JsonValueKind.Object)
                {
                    if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
                    return TextResult(Error("invalid", "Tool arguments must be a JSON object."));
                }
            }
        }
        catch (JsonException)
        {
            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("invalid", "Tool arguments were malformed."));
        }

        if (InstanceSkillTools.IsManagement(call.Name)) return await ExecuteSkillManagementAsync(definition, call, args, admission, remainingOutputBytes, cancellationToken);

        if (LooksLikeSessionMutation(args) || LooksLikeHostPath(args))
        {
            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("forbidden", "Tool arguments are not permitted."));
        }

        if (approvalGrant is not null
            && !string.Equals(call.Name, ToolCatalog.EmailSend, StringComparison.Ordinal)
            && !string.Equals(call.Name, ToolCatalog.HttpRequest, StringComparison.Ordinal))
        {
            var boundHash = ToolActionHash.Compute(call.Name, args);
            if (!string.Equals(boundHash, approvalGrant.ActionHash, StringComparison.Ordinal)
                || !string.Equals(call.Name, approvalGrant.ToolName, StringComparison.Ordinal))
            {
                if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
                return TextResult(Error("stale_approval", "Approval no longer matches the requested action."));
            }
        }

        try
        {
            if (call.Name == ToolCatalog.CredentialsList)
            {
                if (credentials is null || admission?.AgentInstanceId is not Guid owner)
                    return FitResult(remainingOutputBytes, Error("forbidden", "Credential metadata is unavailable."));
                var request = CredentialDiscovery.Parse(args);
                return FitResult(remainingOutputBytes, CredentialDiscovery.Serialize(
                    await credentials.SafeMetadataPageAsync(owner, request.Cursor, request.Limit + 1, cancellationToken), request, remainingOutputBytes));
            }
            if (ToolCatalog.IsIdentityMaintenance(call.Name))
            {
                if (identityMaintenance is null || admission is null)
                {
                    RecordMaintenanceRejection(call.Name);
                    return TextResult(Error("forbidden", "Identity maintenance is unavailable."));
                }
                var result = await identityMaintenance.ExecuteAsync(definition, sessionId, call, args, admission, approvalGrant, cancellationToken);
                return TextResult(AgentCore.Application.Continuity.ContinuityService.Serialize(result));
            }
            if (call.Name is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet)
            {
                if (continuity is null || admission?.AgentInstanceId is not Guid ownerId)
                    return TextResult(Error("forbidden", "Continuity is unavailable."));
                object result;
                if (call.Name == ToolCatalog.ContinuitySearch)
                {
                    if (args.EnumerateObject().Any(p => p.Name is not ("query" or "limit")))
                        return TextResult(Error("invalid", "Unsupported continuity argument."));
                    var query = args.GetProperty("query").GetString();
                    var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 10;
                    var items = await continuity.SearchAsync(ownerId, query, limit, admission.Detached ? null : sessionId, definition, cancellationToken);
                    return TextResult(AgentCore.Application.Continuity.ContinuityService.SearchResult(items,
                        Math.Min(remainingOutputBytes, AgentCore.Application.Continuity.ContinuityService.MaxCharacters)));
                }
                else
                {
                    if (args.EnumerateObject().Any(p => p.Name is not ("kind" or "id" or "afterEntrySequence" or "limit"))
                        || !Enum.TryParse<AgentCore.Application.Continuity.ContinuityKind>(args.GetProperty("kind").GetString(), out var kind)
                        || !Guid.TryParse(args.GetProperty("id").GetString(), out var id))
                        return TextResult(Error("invalid", "Continuity identity is invalid."));
                    result = await continuity.GetAsync(ownerId, kind, id,
                        args.TryGetProperty("afterEntrySequence", out var a) ? a.GetInt64() : 0,
                        args.TryGetProperty("limit", out var l) ? l.GetInt32() : 10, definition, cancellationToken);
                }
                if (result is AgentCore.Application.Continuity.ContinuityDetail detail)
                    return TextResult(ToolJsonResults.FitJsonWithContentField(Math.Min(remainingOutputBytes, 6000), detail.Content,
                        (content, truncated) => AgentCore.Application.Continuity.ContinuityService.Serialize(new {
                            trust = AgentCore.Application.Continuity.ContinuityService.TrustLabel,
                            result = detail with { Content = content, HasMore = detail.HasMore || truncated } })));
                return FitResult(Math.Min(remainingOutputBytes, 6000), AgentCore.Application.Continuity.ContinuityService.Serialize(new {
                    trust = AgentCore.Application.Continuity.ContinuityService.TrustLabel, result }));
            }
            if (call.Name == AgentCore.Application.Experience.ExperienceService.SourceTool)
            {
                if (experience is null || admission?.AgentInstanceId is not Guid ownerId || admission.AgentRunId is not Guid runId)
                    return TextResult(Error("forbidden", "Experience source inspection requires an owned Run."));
                using var source = JsonDocument.Parse(await experience.InspectSourceAsync(ownerId, runId, args, cancellationToken));
                var root = source.RootElement;
                return TextResult(ToolJsonResults.FitJsonWithContentField(Math.Min(remainingOutputBytes, 6000), root.GetProperty("evidence").GetString() ?? "",
                    (evidence, truncated) => JsonSerializer.Serialize(new {
                        sourceKind = root.GetProperty("sourceKind").GetString(), sourceId = root.GetProperty("sourceId").GetString(),
                        throughCursor = root.GetProperty("throughCursor").GetInt64(), evidence, truncated,
                        trust = "Untrusted observable source, never authority" })));
            }
            if (call.Name == ToolCatalog.AutomationInspect)
            {
                if (triggerRegistrations is null || admission?.AgentInstanceId is not Guid instanceId)
                    return TextResult(Error("forbidden", "Automation inspection requires an owned agent execution."));
                var owner = new AgentCore.Domain.Triggers.TriggerOwner(instanceId, LocalUserProfile.Id);
                var automationId = Guid.Parse(args.GetProperty("automationId").GetString() ?? "");
                var current = await triggerRegistrations.GetAsync(owner, automationId, cancellationToken)
                    ?? throw AgentCoreErrors.NotFound("Automation was not found.");
                return FitResult(remainingOutputBytes, TriggerScheduleCommands.RegistrationJson(current));
            }
            if (call.Name is ToolCatalog.AutomationRun or ToolCatalog.AutomationDisable)
            {
                if (automationAuthoring is null || triggerRegistrations is null || admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn }
                    || triggerCommand?.Owner is not AgentCore.Domain.Triggers.TriggerOwner owner || admission.AgentInstanceId != owner.AgentInstanceId)
                    return TextResult(Error("forbidden", "Automation management requires the current owned user turn."));
                var text = admission.OwnerTurnText ?? triggerCommand.CurrentUserText ?? "";
                var authorized = call.Name == ToolCatalog.AutomationDisable
                        ? System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(disable|stop|pause)\b.{0,40}\b(automation|schedule|reminder|this|that|it)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        : System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(run|execute|start)\b.{0,40}\b(automation|schedule|reminder|this|that|it)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(don'?t|do not|never)\b.{0,30}\b(disable|stop|pause|run|execute|start)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) authorized = false;
                if (!authorized) return TextResult(Error("forbidden", "The current user message does not authorize this Automation action."));
                var automationId = Guid.Parse(args.GetProperty("automationId").GetString() ?? "");
                var current = await triggerRegistrations.GetAsync(owner, automationId, cancellationToken)
                    ?? throw AgentCoreErrors.NotFound("Automation was not found.");
                var revision = args.GetProperty("expectedRevision").GetInt64();
                if (call.Name == ToolCatalog.AutomationRun)
                    return TextResult(JsonSerializer.Serialize(new { occurrenceId = (await automationAuthoring.RunNowAsync(owner.AgentInstanceId, automationId, revision, cancellationToken)).OccurrenceId }));
                return TextResult(TriggerScheduleCommands.RegistrationJson(await automationAuthoring.SaveAsync(owner.AgentInstanceId, automationId, revision, false,
                    current.Name, current.Instructions, current.Trigger, current.ModelOverrideCatalogKey, current.ModelOverrideReasoningEffort, cancellationToken)));
            }
            if (call.Name == AgentCore.Application.Experience.ExperienceService.RecordTool)
            {
                if (experience is null || admission?.AgentInstanceId is not Guid instanceId || admission.AgentRunId is not Guid workId)
                    return TextResult(Error("forbidden", "Experience recording requires an owned review Run."));
                return TextResult(await experience.RecordAsync(instanceId, workId, args, cancellationToken));
            }
            if (call.Name == ToolCatalog.ExperienceRecent)
            {
                if (experience is null || admission?.AgentInstanceId is not Guid ownerId)
                    return TextResult(Error("forbidden", "Experience is unavailable."));
                if (args.EnumerateObject().Any(p => p.Name is not ("query" or "experienceId")))
                    return TextResult(Error("invalid", "Experience lookup only accepts query or experienceId."));
                var query = args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
                Guid? recordId = null;
                if (query?.Length > 200) return TextResult(Error("invalid", "Experience query is oversized."));
                if (args.TryGetProperty("experienceId", out var r))
                {
                    if (r.ValueKind != JsonValueKind.String || !Guid.TryParse(r.GetString(), out var id))
                        return TextResult(Error("invalid", "Experience identity is invalid."));
                    recordId = id;
                }
                await experience.RequireInstanceAsync(ownerId, cancellationToken);
                var projection = await experience.RecallAsync(ownerId, cancellationToken, query, recordId);
                return FitResult(remainingOutputBytes, JsonSerializer.Serialize(new { historicalExperience = projection }));
            }
            if (HarnessChatTools.IsHarness(call.Name))
            {
                if (harnessAuthoring is null || admission?.AgentInstanceId is not Guid instanceId)
                    return TextResult(Error("forbidden", "Harness authoring is unavailable in this execution."));
                var result = call.Name == HarnessChatTools.Inspect
                    ? await harnessAuthoring().InspectChatAsync(instanceId, definition.Version, cancellationToken)
                    : await harnessAuthoring().AuthorChatAsync(instanceId, call.Name, args, approvalGrant,
                        admission.HarnessSources ?? [], admission.OwnerTurnText, cancellationToken);
                return FitResult(remainingOutputBytes, JsonSerializer.Serialize(result));
            }
            if ((call.Name.StartsWith("workspace.", StringComparison.Ordinal) || call.Name == ToolCatalog.ArtifactsCreateFromWorkspace))
            {
                if (!await AgentWorkspaceAvailableAsync(sessionId, cancellationToken))
                    return TextResult(Error("forbidden", "Workspace tools require an Agent Instance."));
                if (call.Name == ToolCatalog.WorkspaceCwd)
                    return await ExecuteCwdAsync(definition, sessionId, args, admission?.WorkspaceCwd ?? "/home", cancellationToken);
                args = AgentWorkspacePaths.Arguments(call.Name, args, sessionId, admission?.WorkspaceCwd ?? "/home");
                ProtectCwd(call.Name, args, sessionId, admission?.WorkspaceCwd ?? "/home");
            }
            var executed = call.Name switch
            {
                ToolCatalog.AttachmentsRead => await ReadAttachmentAsync(sessionId, args, remainingOutputBytes, cancellationToken)
                    .ConfigureAwait(false),
                ToolCatalog.KnowledgeRetrieve => FitResult(
                    remainingOutputBytes,
                    await RetrieveKnowledgeAsync(definition, args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceRead => FitResult(
                    remainingOutputBytes,
                    await ReadWorkspaceAsync(definition, sessionId, args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceList => TextResult(
                    await ListWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceWrite => TextResult(
                    await WriteWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspacePatch => TextResult(
                    await PatchWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceSearch => FitResult(
                    remainingOutputBytes,
                    await SearchWorkspaceAsync(definition, sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WorkspaceMkdir or ToolCatalog.WorkspaceCopy or ToolCatalog.WorkspaceMove or ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch => TextResult(
                    await StructureWorkspaceAsync(definition, sessionId, call.Name, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.ArtifactsCreate => TextResult(
                    await CreateArtifactAsync(sessionId, args, admission?.AgentRunId, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.ArtifactsCreateFromWorkspace => TextResult(
                    await CreateArtifactFromWorkspaceAsync(definition, sessionId, args, admission?.AgentRunId, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.ArtifactsVerify => TextResult(
                    await VerifyArtifactAsync(sessionId, args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.SandboxRun => FitResult(
                    remainingOutputBytes,
                    await RunSandboxAsync(definition, sessionId, args, remainingOutputBytes, admission?.AgentRunId, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WebSearch => FitResult(
                    remainingOutputBytes,
                    await SearchWebAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WebFetch => FitResult(
                    remainingOutputBytes,
                    await FetchWebAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.HttpRequest => FitResult(
                    remainingOutputBytes,
                    await ExecuteHttpRequestAsync(args, approvalGrant, cancellationToken).ConfigureAwait(false)),
                _ when ToolCatalog.IsBrowserTool(call.Name) => await ExecuteBrowserAsync(
                    definition, sessionId, call.Name, args, admission, remainingOutputBytes, cancellationToken).ConfigureAwait(false),
                ToolCatalog.DemoSensitiveAction => TextResult(
                    ExecuteDemoSensitiveAction(sessionId, args, approvalGrant)),
                ToolCatalog.EmailSearch => FitResult(
                    remainingOutputBytes,
                    await SearchEmailAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.EmailRead => FitResult(
                    remainingOutputBytes,
                    await ReadEmailAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.EmailCreateDraft => TextResult(
                    await CreateEmailDraftAsync(args, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.EmailSend => TextResult(
                    await SendEmailDraftAsync(args, approvalGrant, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.AutomationCreate or ToolCatalog.AutomationList
                    or ToolCatalog.AutomationUpdate or ToolCatalog.AutomationDelete => await TriggerScheduleCommands.ExecuteAsync(
                        definition,
                        triggerRegistrations,
                        call.Name,
                        args,
                        triggerCommand,
                        cancellationToken,
                        _triggerAuthorizer,
                        _agentInstances,
                        _agentDefinitions,
                        _profiles, automationAuthoring).ConfigureAwait(false),
                _ => TextResult(Error("forbidden", "Tool is not permitted for this role."))
            };
            return (call.Name.StartsWith("workspace.", StringComparison.Ordinal) || call.Name is ToolCatalog.ArtifactsCreateFromWorkspace or ToolCatalog.SandboxRun)
                ? executed with { Text = AgentWorkspacePaths.Project(executed.Text) } : executed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (call.Name is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet
            && ex is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return TextResult(Error("invalid", "Continuity arguments are invalid."));
        }
        catch (ArgumentException) when (ToolCatalog.IsIdentityMaintenance(call.Name))
        { return FitResult(remainingOutputBytes, Error("invalid", "Consolidation content is malformed or exceeds its bounds.")); }
        catch (AgentCoreException ex)
        {
            return FitResult(remainingOutputBytes, ex.DiagnosticId is { } diagnosticId
                ? JsonSerializer.Serialize(new { error = ex.Code, message = ex.Message, diagnosticId })
                : Error(ex.Code, ex.Message));
        }
    }

    private static ToolExecutionResult TextResult(string text) => ToolExecutionResult.FromText(text);

    private static ToolExecutionResult FitResult(int remainingOutputBytes, string raw) =>
        TextResult(ToolJsonResults.FitToBudget(remainingOutputBytes, raw));

    private async Task<string> RetrieveKnowledgeAsync(
        AgentDefinition definition,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (knowledge is null || !TryString(args, "identity", out var identity))
        {
            return Error("invalid", "identity is required.");
        }

        var document = await knowledge.RetrieveAsync(definition, identity, cancellationToken).ConfigureAwait(false);
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            document.Content,
            (content, truncated) => JsonSerializer.Serialize(new
            {
                identity = document.Identity,
                title = document.Title,
                citation = document.Citation,
                content,
                truncated
            }));
    }

    private async Task<ToolExecutionResult> ReadAttachmentAsync(
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (attachments is null || !TryString(args, "attachmentId", out var raw) || !Guid.TryParse(raw, out var attachmentId))
        {
            return TextResult(Error("invalid", "attachmentId is required."));
        }

        var record = await attachments.GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return TextResult(Error("notFound", "Attachment was not found."));
        }

        if (AttachmentMedia.IsImage(record.ContentType))
        {
            return await ReadHistoricalImageAsync(sessionId, record, remainingOutputBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(AttachmentMedia.NormalizeContentType(record.ContentType), "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (processor is null)
            {
                return TextResult(JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind = "pdf",
                    note = "PDF extraction is unavailable."
                }));
            }

            var processed = await processor.ProcessTurnAsync(sessionId, [attachmentId], cancellationToken)
                .ConfigureAwait(false);
            var extracted = processed.FirstOrDefault(item => item.Kind == AttachmentProcessKind.ExtractedText);
            if (extracted is null || string.IsNullOrEmpty(extracted.Text))
            {
                return TextResult(JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind = "pdf",
                    note = "PDF content could not be extracted."
                }));
            }

            return TextResult(ToolJsonResults.FitJsonWithContentField(
                remainingOutputBytes,
                extracted.Text,
                (content, truncated) => JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    kind = "pdf",
                    provenance = extracted.Provenance,
                    truncated,
                    content
                })));
        }

        if (!AttachmentMedia.IsReadableText(record.ContentType))
        {
            return TextResult(JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                byteSize = record.ByteSize,
                kind = "binary",
                note = "This attachment type is not readable as UTF-8 text through attachments.read."
            }));
        }

        await using var stream = await attachments.OpenContentAsync(sessionId, attachmentId, cancellationToken)
            .ConfigureAwait(false);
        var budget = Math.Max(0, Math.Min(remainingOutputBytes, ToolLimits.MaxOutputBytes));
        using var buffer = new MemoryStream();
        var block = new byte[Math.Min(8192, Math.Max(1, budget))];
        while (buffer.Length < budget)
        {
            var read = await stream.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, budget - buffer.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(block, 0, read);
        }

        var text = DecodeText(buffer.ToArray());
        var byteTruncated = buffer.Length < record.ByteSize;
        return TextResult(ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            text,
            (content, truncated) => JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId,
                displayName = record.DisplayName,
                contentType = record.ContentType,
                truncated = byteTruncated || truncated,
                content
            })));
    }

    private async Task<ToolExecutionResult> ReadHistoricalImageAsync(
        Guid sessionId,
        AttachmentRecord record,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (processor is null)
        {
            return TextResult(AttachmentProcessingFailed());
        }

        var processed = await processor.ProcessTurnAsync(sessionId, [record.AttachmentId], cancellationToken)
            .ConfigureAwait(false);
        var image = processed.FirstOrDefault(item =>
            item.AttachmentId == record.AttachmentId && item.Kind == AttachmentProcessKind.Image);
        if (image?.StrippedImage is not { Length: > 0 } sanitized)
        {
            return TextResult(AttachmentProcessingFailed());
        }

        var metadata = JsonSerializer.Serialize(new
        {
            attachmentId = record.AttachmentId,
            displayName = record.DisplayName,
            contentType = image.ContentType,
            kind = "image",
            processorVersion = image.ProcessorVersion,
            contentProvided = true
        });
        if (Encoding.UTF8.GetByteCount(metadata) > Math.Max(0, remainingOutputBytes))
        {
            return TextResult(ToolJsonResults.FitToBudget(
                remainingOutputBytes,
                JsonSerializer.Serialize(new
                {
                    error = "output_limit",
                    message = "Image metadata could not fit the remaining tool output budget."
                })));
        }

        return new ToolExecutionResult(
            metadata,
            [new ModelImageContent(image.ContentType, sanitized, image.DisplayName)]);
    }

    private static string AttachmentProcessingFailed() =>
        JsonSerializer.Serialize(new
        {
            error = "attachment_processing_failed",
            message = "The image could not be prepared for model input."
        });

    private async Task<string> ReadWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (workspace is null || !TryString(args, "path", out var path))
        {
            return Error("invalid", "path is required.");
        }

        if (!TryResolveWorkspacePath(path, sessionId, out path, out var pathError))
        {
            return pathError;
        }

        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        AgentWorkspaceContent? home = null;
        if (AgentCore.Application.Workspaces.AgentHomePath.IsHome(path) && agentWorkspace is not null)
            home = await agentWorkspace.ReadAsync(await agentWorkspace.SessionOwnerAsync(sessionId, cancellationToken), path: path, cancellationToken: cancellationToken);
        var content = home is null ? await ReadExecutionWorkspaceAsync(sessionId, definition, path, cancellationToken).ConfigureAwait(false)
            : new WorkspaceContent(path, home.Item.ContentType, home.Bytes);
        var homeItem = home?.Item;
        var take = Math.Min(content.Bytes.Length, Math.Max(0, remainingOutputBytes));
        var decoded = DecodeText(content.Bytes.AsSpan(0, take).ToArray());
        var byteTruncated = take < content.Bytes.Length;
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            decoded,
            (body, truncated) => JsonSerializer.Serialize(new
            {
                path = content.LogicalPath,
                contentType = content.ContentType,
                homeItem,
                truncated = byteTruncated || truncated,
                content = body
            }));
    }

    private async Task<string> ListWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            return Error("unavailable", "Workspace is unavailable.");
        }

        var path = ".";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            path = pathElement.GetString() ?? path;
        }

        if (!TryResolveWorkspacePath(path, sessionId, out path, out var pathError))
        {
            return pathError;
        }

        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WorkspaceNode> nodes;
        string? treeSha256 = null;
        if (AgentCore.Application.Workspaces.AgentHomePath.IsHome(path) && agentWorkspace is not null)
        {
            var projection = await agentWorkspace.ListProjectionAsync(await agentWorkspace.SessionOwnerAsync(sessionId, cancellationToken), path, cancellationToken);
            var writable = await agentWorkspace.SessionWritableAsync(sessionId, cancellationToken);
            nodes = writable ? projection.Nodes.Select(n => n with { Writable = true }).ToArray() : projection.Nodes;
            treeSha256 = projection.TreeSha256;
        }
        else nodes = await ListExecutionWorkspaceAsync(sessionId, definition, path, cancellationToken).ConfigureAwait(false);
        var truncated = nodes.Count > WorkspaceLimits.MaxListEntries;
        var slice = truncated ? nodes.Take(WorkspaceLimits.MaxListEntries).ToArray() : nodes;
        return JsonSerializer.Serialize(new
        {
            path,
            truncated,
            treeSha256,
            entries = slice.Select(node => new
            {
                path = node.LogicalPath,
                directory = node.Directory,
                byteSize = node.ByteSize,
                writable = node.Writable
            })
        });
    }

    private async Task<string> PatchWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null
            || !TryString(args, "path", out var path)
            || !TryString(args, "expectedSha256", out var expectedSha256)
            || !args.TryGetProperty("edits", out var editsElement)
            || editsElement.ValueKind != JsonValueKind.Array)
        {
            return Error("invalid", "path, expectedSha256, and edits are required.");
        }

        if (editsElement.GetArrayLength() > WorkspaceLimits.MaxPatchEdits)
        {
            return Error("invalid", "At most 32 edits are permitted per patch.");
        }

        var edits = new List<WorkspaceTextEdit>();
        foreach (var item in editsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryString(item, "oldText", out var oldText)
                || !TryString(item, "newText", out var newText))
            {
                return Error("invalid", "Each edit requires oldText and newText.");
            }

            if (string.IsNullOrEmpty(oldText))
            {
                return Error("invalid", "Each edit oldText must be non-empty.");
            }

            edits.Add(new WorkspaceTextEdit(oldText, newText));
        }

        if (edits.Count == 0)
        {
            return Error("invalid", "At least one edit is required.");
        }

        if (!TryResolveWorkspacePath(path, sessionId, out path, out var pathError))
        {
            return pathError;
        }

        if (AgentCore.Application.Workspaces.AgentHomePath.IsHome(path))
        {
            if (agentWorkspace is null) return Error("unavailable", "Agent Workspace is unavailable.");
            var patched = await agentWorkspace.PatchAsync(sessionId, path, expectedSha256, edits, cancellationToken);
            return JsonSerializer.Serialize(new { path = patched.LogicalPath, previousSha256 = patched.PreviousSha256Hex,
                newSha256 = patched.NewSha256Hex, byteSize = patched.ByteSize, editsApplied = patched.EditsApplied });
        }
        var result = await workspace.PatchTextAsync(
                sessionId,
                definition,
                path,
                expectedSha256,
                edits,
                cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            path = result.LogicalPath,
            previousSha256 = result.PreviousSha256Hex,
            newSha256 = result.NewSha256Hex,
            byteSize = result.ByteSize,
            editsApplied = result.EditsApplied
        });
    }

    private async Task<string> WriteWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (args.EnumerateObject().Any(p => p.Name is not ("path" or "content" or "expectedRevision" or "expectedSha256")))
            return Error("invalid", "Write accepts path, content and optional expected durable revision/hash.");
        if (workspace is null
            || !TryString(args, "path", out var path)
            || !TryString(args, "content", out var content))
        {
            return Error("invalid", "path and content are required.");
        }

        if (!TryResolveWorkspacePath(path, sessionId, out path, out var pathError))
        {
            return pathError;
        }

        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (AgentCore.Application.Workspaces.AgentHomePath.IsHome(path))
        {
            if (agentWorkspace is null) return Error("unavailable", "Agent Workspace is unavailable.");
            var item = await agentWorkspace.WriteAsync(sessionId, path, "text/plain; charset=utf-8", bytes,
                ExpectedRevision(args), ExpectedHash(args), cancellationToken);
            return JsonSerializer.Serialize(new { path, bytes = bytes.Length, homeItem = item });
        }
        await workspace.WriteAsync(sessionId, path, bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { path, bytes = bytes.Length });
    }

    private async Task<string> CreateArtifactAsync(
        Guid sessionId,
        JsonElement args,
        Guid? agentRunId,
        CancellationToken cancellationToken)
    {
        if (artifacts is null || !TryString(args, "displayName", out var displayName) || !TryString(args, "content", out var content))
        {
            return Error("invalid", "displayName and content are required.");
        }

        TryString(args, "contentType", out var contentType);

        var created = await artifacts.CreateAsync(
                sessionId,
                displayName,
                string.IsNullOrWhiteSpace(contentType) ? "text/markdown" : contentType,
                Encoding.UTF8.GetBytes(content),
                sourceAttachmentId: null,
                workspaceLogicalPath: null,
                cancellationToken, agentRunId)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            artifactId = created.ArtifactId,
            displayName = created.DisplayName,
            sha256Hex = created.Sha256Hex,
            sourceAttachmentId = created.SourceAttachmentId
        });
    }

    private async Task<string> VerifyArtifactAsync(
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (artifacts is null || !TryString(args, "artifactId", out var raw) || !Guid.TryParse(raw, out var artifactId))
        {
            return Error("invalid", "artifactId is required.");
        }

        var record = await artifacts.GetAsync(sessionId, artifactId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Error("notFound", "Artifact was not found.");
        }

        return JsonSerializer.Serialize(new
        {
            artifactId = record.ArtifactId,
            displayName = record.DisplayName,
            contentType = record.ContentType,
            byteSize = record.ByteSize,
            sha256Hex = record.Sha256Hex,
            sourceAttachmentId = record.SourceAttachmentId,
            workspaceLogicalPath = record.WorkspaceLogicalPath,
            createdAt = record.CreatedAt
        });
    }

    private async Task<string> CreateArtifactFromWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        Guid? agentRunId,
        CancellationToken cancellationToken)
    {
        if (workspace is null || artifacts is null || !TryString(args, "path", out var path) || !TryString(args, "displayName", out var displayName))
        {
            return Error("invalid", "path and displayName are required.");
        }

        if (!TryResolveWorkspacePath(path, sessionId, out path, out var pathError))
        {
            return pathError;
        }

        if (!path.StartsWith("/workspace/", StringComparison.Ordinal)
            && !AgentCore.Application.Workspaces.AgentHomePath.IsHome(path))
        {
            return Error("path_outside_workspace", WorkspaceLogicalPath.OutsideWorkspaceMessage);
        }

        TryString(args, "contentType", out var contentType);
        await workspace.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var content = await ReadExecutionWorkspaceAsync(sessionId, definition, path, cancellationToken).ConfigureAwait(false);
        var created = await artifacts.CreateAsync(
                sessionId,
                displayName,
                string.IsNullOrWhiteSpace(contentType) ? content.ContentType : contentType,
                content.Bytes,
                sourceAttachmentId: null,
                workspaceLogicalPath: AgentWorkspacePaths.Public(content.LogicalPath),
                cancellationToken, agentRunId)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            artifactId = created.ArtifactId,
            displayName = created.DisplayName,
            contentType = created.ContentType,
            byteSize = created.ByteSize,
            sha256Hex = created.Sha256Hex,
            workspaceLogicalPath = created.WorkspaceLogicalPath
        });
    }

    private async Task<string> SearchWebAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (webSearch is null || !webSearch.IsAvailable)
        {
            return Error("unavailable", "Web search is not configured.");
        }

        if (!TryString(args, "query", out var query))
        {
            return Error("invalid", "query is required.");
        }

        if (query.Length > WebToolLimits.MaxSearchQueryLength)
        {
            return Error("invalid", $"query must be at most {WebToolLimits.MaxSearchQueryLength} characters.");
        }

        var limit = WebToolLimits.DefaultSearchLimit;
        if (args.TryGetProperty("limit", out var limitProperty))
        {
            if (limitProperty.ValueKind != JsonValueKind.Number || !limitProperty.TryGetInt32(out limit))
            {
                return Error("invalid", "limit must be an integer.");
            }
        }

        limit = Math.Clamp(limit, 1, WebToolLimits.MaxSearchLimit);
        var started = Stopwatch.GetTimestamp();
        var result = await webSearch
            .SearchAsync(new WebSearchRequest(query, limit), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("web.search", RuntimeTelemetry.ElapsedMs(started));
        var items = result.Results.Select(item => new
        {
            title = item.Title,
            url = item.Url,
            snippet = item.Snippet
        }).ToArray();
        return ToolJsonResults.FitToBudget(
            remainingOutputBytes,
            JsonSerializer.Serialize(new
            {
                untrustedWebContent = true,
                query,
                results = items,
                truncated = result.Truncated
            }));
    }

    private async Task<string> FetchWebAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (publicWebFetcher is null)
        {
            return Error("unavailable", "Web fetch is not configured.");
        }

        if (!TryString(args, "url", out var urlText))
        {
            return Error("invalid", "url is required.");
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return Error("invalid", "url must be an absolute http or https URL.");
        }

        var started = Stopwatch.GetTimestamp();
        var result = await publicWebFetcher
            .FetchAsync(new PublicWebFetchRequest(url), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("web.fetch", RuntimeTelemetry.ElapsedMs(started));
        if (result.ErrorCode is not null)
        {
            return JsonSerializer.Serialize(new
            {
                untrustedWebContent = true,
                finalUrl = result.FinalUrl,
                error = result.ErrorCode,
                message = result.ErrorMessage
            });
        }

        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            result.Text,
            (text, truncated) => JsonSerializer.Serialize(new
            {
                untrustedWebContent = true,
                finalUrl = result.FinalUrl,
                contentType = result.ContentType,
                text,
                truncated = truncated || result.Truncated
            }));
    }

    private async Task<string> RunSandboxAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        int remainingOutputBytes,
        Guid? agentRunId,
        CancellationToken cancellationToken)
    {
        if (sandbox is null)
        {
            return Error("unavailable", "Container sandbox is unavailable.");
        }

        if (!TryString(args, "verb", out var verb))
        {
            return Error("invalid", "verb is required.");
        }

        var arguments = new List<string>();
        if (args.TryGetProperty("arguments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return Error("invalid", "arguments must be strings.");
                }

                arguments.Add(item.GetString() ?? "");
            }
        }

        TryString(args, "exportPath", out var export);
        if (!string.IsNullOrWhiteSpace(export)) export = AgentWorkspacePaths.Resolve(export, sessionId, "/working");
        if (verb == "cat" && arguments.Count == 1) arguments[0] = AgentWorkspacePaths.Resolve(arguments[0], sessionId, "/working");
        if (!string.IsNullOrWhiteSpace(export)
            && !TryResolveWorkspacePath(export, sessionId, out export, out var exportError))
        {
            return exportError;
        }

        if (string.Equals(verb, "cat", StringComparison.OrdinalIgnoreCase) && arguments.Count == 1)
        {
            if (!TryResolveWorkspacePath(arguments[0], sessionId, out var resolvedCatPath, out var catError))
            {
                return catError;
            }

            arguments[0] = resolvedCatPath;
        }

        var started = Stopwatch.GetTimestamp();
        var result = await sandbox.RunAsync(
                new SandboxRequest(
                    sessionId,
                    Guid.CreateVersion7(),
                    definition,
                    verb,
                    arguments,
                    string.IsNullOrWhiteSpace(export) ? null : export, agentRunId),
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("sandbox", RuntimeTelemetry.ElapsedMs(started), verb);
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            result.Output,
            (output, truncated) => JsonSerializer.Serialize(new
            {
                ok = result.Succeeded,
                exitCode = result.ExitCode,
                output,
                truncated = truncated || result.Truncated,
                artifactId = result.ArtifactId,
                message = result.SafeMessage
            }));
    }

    private static bool LooksLikeSessionMutation(JsonElement args)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (property.Name.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("mutate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("revision", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("persist", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("history", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeHostPath(JsonElement args)
    {
        if (!TryString(args, "path", out var path) && !TryString(args, "file", out path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            return true;
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        return Path.IsPathRooted(normalized) && !normalized.StartsWith('/');
    }

    private static bool TryString(JsonElement args, string name, out string value)
    {
        value = "";
        if (!args.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Convert.ToBase64String(bytes);
        }
    }

    public async Task<EmailSendApprovalPrepareResult> PrepareEmailSendApprovalAsync(
        JsonElement args,
        CancellationToken cancellationToken = default)
    {
        if (emailProvider is null || !emailProvider.IsAvailable)
        {
            return new EmailSendApprovalPrepareResult(null, Error("unavailable", "Email is not configured."));
        }

        if (!TryString(args, "draftId", out var draftId))
        {
            return new EmailSendApprovalPrepareResult(null, Error("invalid", "draftId is required."));
        }

        var draft = await emailProvider.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return new EmailSendApprovalPrepareResult(null, Error("notFound", "Draft was not found."));
        }

        var normalized = EmailDraftNormalizer.Normalize(draft);
        var actionHash = EmailDraftNormalizer.ComputeSendActionHash(normalized);
        var summary = ToolApprovalPreview.BoundSummary($"Send email to {FormatRecipientPreview(normalized.To)}");
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["To"] = ToolApprovalPreview.BoundDetailValue(FormatRecipientPreview(normalized.To)),
            ["Cc"] = ToolApprovalPreview.BoundDetailValue(FormatRecipientPreview(normalized.Cc)),
            ["Bcc"] = ToolApprovalPreview.BoundDetailValue(FormatRecipientPreview(normalized.Bcc)),
            ["Subject"] = ToolApprovalPreview.BoundDetailValue(TruncatePreview(normalized.Subject, EmailToolLimits.MaxPreviewSubjectLength)),
            ["Body"] = ToolApprovalPreview.BoundDetailValue(normalized.Body)
        };
        return new EmailSendApprovalPrepareResult(
            new EmailSendApprovalPreparation(actionHash, summary, details),
            null);
    }

    private async Task<string> SearchEmailAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (emailProvider is null || !emailProvider.IsAvailable)
        {
            return Error("unavailable", "Email is not configured.");
        }

        if (!TryString(args, "query", out var query))
        {
            return Error("invalid", "query is required.");
        }

        if (query.Length > EmailToolLimits.MaxSearchQueryLength)
        {
            return Error("invalid", $"query must be at most {EmailToolLimits.MaxSearchQueryLength} characters.");
        }

        var limit = EmailToolLimits.DefaultSearchLimit;
        if (args.TryGetProperty("limit", out var limitProperty))
        {
            if (limitProperty.ValueKind != JsonValueKind.Number || !limitProperty.TryGetInt32(out limit))
            {
                return Error("invalid", "limit must be an integer.");
            }
        }

        limit = Math.Clamp(limit, 1, EmailToolLimits.MaxSearchLimit);
        var started = Stopwatch.GetTimestamp();
        var result = await emailProvider
            .SearchAsync(new EmailSearchRequest(query, limit), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("email.search", RuntimeTelemetry.ElapsedMs(started));
        var items = result.Results.Select(item => new
        {
            messageId = item.MessageId,
            threadId = item.ThreadId,
            from = item.From,
            subject = item.Subject,
            date = item.Date,
            snippet = item.Snippet
        }).ToArray();
        return ToolJsonResults.FitToBudget(
            remainingOutputBytes,
            JsonSerializer.Serialize(new { query, results = items, truncated = result.Truncated }));
    }

    private async Task<string> ReadEmailAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken)
    {
        if (emailProvider is null || !emailProvider.IsAvailable)
        {
            return Error("unavailable", "Email is not configured.");
        }

        if (!TryString(args, "messageId", out var messageId))
        {
            return Error("invalid", "messageId is required.");
        }

        var started = Stopwatch.GetTimestamp();
        var message = await emailProvider
            .ReadAsync(new EmailReadRequest(messageId), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("email.read", RuntimeTelemetry.ElapsedMs(started));
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            message.Body,
            (body, truncated) => JsonSerializer.Serialize(new
            {
                messageId = message.MessageId,
                threadId = message.ThreadId,
                from = message.From,
                to = message.To,
                cc = message.Cc,
                date = message.Date,
                subject = message.Subject,
                body,
                bodyTruncated = truncated || message.BodyTruncated,
                attachments = message.Attachments.Select(attachment => new
                {
                    attachmentId = attachment.AttachmentId,
                    fileName = attachment.FileName,
                    contentType = attachment.ContentType,
                    byteSize = attachment.ByteSize
                })
            }));
    }

    private async Task<string> CreateEmailDraftAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (emailProvider is null || !emailProvider.IsAvailable)
        {
            return Error("unavailable", "Email is not configured.");
        }

        if (!TryReadAddressList(args, "to", out var to, required: true)
            || !TryReadAddressList(args, "cc", out var cc, required: false)
            || !TryReadAddressList(args, "bcc", out var bcc, required: false))
        {
            return Error("invalid", "to must be a non-empty array of addresses.");
        }

        if (!TryString(args, "subject", out var subject))
        {
            return Error("invalid", "subject is required.");
        }

        if (!TryString(args, "body", out var body))
        {
            return Error("invalid", "body is required.");
        }

        if (subject.Length > EmailToolLimits.MaxSubjectLength)
        {
            return Error("invalid", $"subject must be at most {EmailToolLimits.MaxSubjectLength} characters.");
        }

        if (!EmailHeaderSafety.IsSafeSubject(subject))
        {
            return Error("invalid", "subject contains control characters.");
        }

        if (body.Length > EmailToolLimits.MaxBodyLength)
        {
            return Error("invalid", $"body must be at most {EmailToolLimits.MaxBodyLength} characters.");
        }

        if (!EmailHeaderSafety.IsSafeBody(body))
        {
            return Error("invalid", "body contains control characters.");
        }

        var started = Stopwatch.GetTimestamp();
        var created = await emailProvider
            .CreateDraftAsync(new EmailCreateDraftRequest(to, cc, bcc, subject, body), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("email.create_draft", RuntimeTelemetry.ElapsedMs(started));
        return JsonSerializer.Serialize(new
        {
            draftId = created.Draft.DraftId,
            to = created.Draft.To,
            cc = created.Draft.Cc,
            bcc = created.Draft.Bcc,
            subject = created.Draft.Subject
        });
    }

    private async Task<string> SendEmailDraftAsync(
        JsonElement args,
        ToolApprovalGrant? approvalGrant,
        CancellationToken cancellationToken)
    {
        if (approvalGrant is null)
        {
            return Error("approval_required", "Tool execution requires explicit approval.");
        }

        if (emailProvider is null || !emailProvider.IsAvailable)
        {
            return Error("unavailable", "Email is not configured.");
        }

        if (!TryString(args, "draftId", out var draftId))
        {
            return Error("invalid", "draftId is required.");
        }

        var draft = await emailProvider.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return Error("notFound", "Draft was not found.");
        }

        var normalized = EmailDraftNormalizer.Normalize(draft);
        var actionHash = EmailDraftNormalizer.ComputeSendActionHash(normalized);
        if (!string.Equals(actionHash, approvalGrant.ActionHash, StringComparison.Ordinal)
            || !string.Equals(approvalGrant.ToolName, ToolCatalog.EmailSend, StringComparison.Ordinal))
        {
            return Error("stale_approval", "Approval no longer matches the draft content.");
        }

        var sendClaim = new EmailSendLedger.ClaimKey(approvalGrant.ResponseId, approvalGrant.ApprovalId);
        if (!EmailSendLedger.TryBegin(sendClaim))
        {
            return Error("duplicate", "This approval was already used to send email.");
        }

        var started = Stopwatch.GetTimestamp();
        var sendStarted = false;
        try
        {
            sendStarted = true;
            var result = await emailProvider
                .SendDraftAsync(new EmailSendDraftRequest(draftId, normalized), cancellationToken)
                .ConfigureAwait(false);
            RuntimeTelemetry.Record("email.send", RuntimeTelemetry.ElapsedMs(started));
            switch (result.Outcome)
            {
                case EmailSendOutcome.Sent:
                    EmailSendLedger.CompleteSent(sendClaim);
                    break;
                case EmailSendOutcome.Failed:
                    EmailSendLedger.MarkDefinitelyFailed(sendClaim);
                    break;
                default:
                    EmailSendLedger.MarkIndeterminate(sendClaim);
                    break;
            }

            return JsonSerializer.Serialize(new
            {
                outcome = result.Outcome.ToString().ToLowerInvariant(),
                providerMessageId = result.ProviderMessageId,
                error = result.ErrorCode,
                message = result.ErrorMessage
            });
        }
        catch
        {
            if (sendStarted)
            {
                EmailSendLedger.MarkIndeterminate(sendClaim);
            }
            else
            {
                EmailSendLedger.MarkDefinitelyFailed(sendClaim);
            }

            throw;
        }
    }

    private static bool TryReadAddressList(
        JsonElement args,
        string name,
        out IReadOnlyList<string> addresses,
        bool required)
    {
        addresses = [];
        if (!args.TryGetProperty(name, out var property))
        {
            return !required;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = item.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!EmailHeaderSafety.TryValidateAddress(value, out var address))
            {
                return false;
            }

            list.Add(address);
            if (list.Count > EmailToolLimits.MaxRecipientsPerField)
            {
                return false;
            }
        }

        if (required && list.Count == 0)
        {
            return false;
        }

        addresses = list;
        return true;
    }

    private static string FormatRecipientPreview(IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0)
        {
            return "(none)";
        }

        var preview = string.Join(", ", addresses.Take(EmailToolLimits.MaxPreviewRecipients));
        if (addresses.Count > EmailToolLimits.MaxPreviewRecipients)
        {
            preview += ", …";
        }

        return preview;
    }

    private static string TruncatePreview(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string ExecuteDemoSensitiveAction(
        Guid sessionId,
        JsonElement args,
        ToolApprovalGrant? approvalGrant)
    {
        if (approvalGrant is null)
        {
            return Error("approval_required", "Tool execution requires explicit approval.");
        }

        if (!TryString(args, "label", out var label))
        {
            return Error("invalid", "label is required.");
        }

        var actionHash = ToolActionHash.Compute(ToolCatalog.DemoSensitiveAction, args);
        if (!string.Equals(actionHash, approvalGrant.ActionHash, StringComparison.Ordinal))
        {
            return Error("stale_approval", "Approval no longer matches the requested action.");
        }

        return DemoSensitiveActionStore.TryExecute(sessionId, approvalGrant.ApprovalId, label, out var result)
            ? result
            : result;
    }

    private static bool TryResolveWorkspacePath(
        string raw,
        Guid sessionId,
        out string canonical,
        out string jsonError)
    {
        if (WorkspaceLogicalPath.TryResolve(raw, sessionId, out canonical, out var code, out var message))
        {
            jsonError = string.Empty;
            return true;
        }

        jsonError = Error(code, message);
        return false;
    }

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
