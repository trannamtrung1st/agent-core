using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Agents;
using AgentCore.Application.Email;
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
    AdminAutomationAuthoringService? automationAuthoring = null, AgentInstanceSkillService? instanceSkills = null, ICoreEventStore? eventCoverage = null, InheritedDefinitionResourceCatalog? inheritedResources = null, SelfManagementService? selfManagement = null,
    AgentInstanceSettingsService? instanceSettings = null, AgentInstanceResourceService? instanceResources = null, IAgentCredentialBindingStore? credentialBindings = null,
    AdminAgentInstanceService? instanceAdmin = null, AgentCore.Application.Email.EmailAccountService? emailAccounts = null, IEmailStore? emailStore = null, IAgentRunStore? emailRuns = null, ILogger<SessionToolExecutor>? logger = null, IDiagnosticIdSource? diagnosticIds = null, IOfficeProcessor? officeProcessor = null, AgentCore.Application.Drive.DriveService? driveService = null, ISandboxService? sandboxService = null, ISandboxStore? sandboxStore = null, ISandboxImageAuthority? sandboxAuthority = null, ISandboxReadiness? sandboxReadiness = null, AgentCore.Application.Workspaces.SessionWorkingCleanupService? workingCleanup = null, Func<RuntimeStateService?>? runtimeState = null, AgentCore.Application.Security.TenantToolAuthorization? tenantAuthorization = null)
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
        triggerAuthorizer ?? new UnavailableTriggerCommandAuthorizer();

    public async ValueTask<AutomationCommandPreparation> PrepareAutomationAsync(AgentDefinition definition, ModelToolCall call,
        JsonElement args, TriggerCommandContext command, bool verify, CancellationToken ct)
    {
        if (await LegacyApprovalPreparationErrorAsync(command.SessionId, null, ct).ConfigureAwait(false) is { } denied)
            return new(null, denied);
        var prepared = await TriggerScheduleCommands.PrepareAsync(definition, triggerRegistrations, call.Name, args, command, ct);
        return prepared.Request is null || !verify ? prepared
            : prepared with { Verdict = await _triggerAuthorizer.AuthorizeAsync(prepared.Request, ct) };
    }

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
        if (instanceId is not Guid id || _agentInstances is null || _agentDefinitions is null) return null;
        var instance = await _agentInstances.FindAsync(id, ct);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active) return null;
        var definition = await _agentDefinitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct);
        if (definition is null) return null;
        var policy = ManagementPolicyResolver.Resolve(definition, instance);
        if (policy.Frozen || policy.Mode == HarnessManagementMode.Disabled) return null;
        return new(policy, instance.HarnessManagement?.PolicyRevision ?? 1, instance.ActiveVersion);
    }

    public async ValueTask<AgentCore.Application.Execution.ResolvedAgentRunConfiguration> ResolveRunConfigurationAsync(SessionSnapshot session, CancellationToken ct)
    {
        if (_agentInstances is not null && _agentDefinitions is not null && definitionResources is not null)
            return await new AgentCore.Application.Execution.AgentRunConfigurationResolver(_agentInstances, _agentDefinitions, definitionResources, inheritedResources, credentialBindings, sandboxStore, sandboxAuthority, sandboxReadiness, browser).ResolveAsync(session.AgentInstanceId, ct);
        // Isolated fixtures can supply their immutable Definition directly. Production wires all stores.
        var owner = _agentInstances is null ? null : await _agentInstances.FindAsync(session.AgentInstanceId, ct);
        var bindings = credentialBindings is null ? [] : await credentialBindings.ListBindingsAsync(session.AgentInstanceId, ct);
        return new(new(session.Definition, 0, session.PinnedPersonaRevision ?? 0, [], credentialBindings: bindings), session.PinnedPersona ?? session.Definition.Identity,
            await ResolveSkillCatalogAsync(session.AgentInstanceId, session.Definition, ct), owner?.ExecutionBudgets);
    }

    public async ValueTask<EffectiveExecutionBudget> ResolveExecutionBudgetAsync(Guid owner, AgentDefinition definition,
        bool interactive, CancellationToken ct)
    {
        var instance = _agentInstances is null ? null : await _agentInstances.FindAsync(owner, ct).ConfigureAwait(false);
        return AgentCore.Application.Execution.ExecutionBudgetResolver.Resolve(definition, instance?.ExecutionBudgets, interactive);
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
        if (tenantAuthorization is not null && await tenantAuthorization.PolicyAsync(sessionId, call, admission, ct).ConfigureAwait(false) is { } tenantPolicy) return tenantPolicy;
        if (admission?.TenantId is not null || (IamToolContracts.IsInspection(call.Name) || call.Name is ToolCatalog.RequestAuthorization or TenantPreferenceToolContract.Name)) return ToolPolicyDecision.Deny;
        if (admission?.AgentInstanceId is { } isolationOwner)
            definition = await RestrictExecutionDefinitionAsync(definition, isolationOwner, ct);
        if (ManagementTools.IsManagement(call.Name) && admission is not null)
            admission = admission with { Harness = await HarnessContextAsync(admission.AgentInstanceId, ct) };
        if (!ToolCatalog.IsIdentityMaintenance(call.Name)) return ManagementDecision(definition, call.Name, args, admission, null,
            EvaluateExecutionPolicy(definition, call.Name, admission: admission));
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
        if (await LegacyApprovalPreparationErrorAsync(sessionId, admission, ct).ConfigureAwait(false) is { } denied)
            return (null, denied);
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
        using var scope = logger?.BeginScope("Tool={Tool} SessionId={SessionId} AgentInstanceId={AgentInstanceId} AgentRunId={AgentRunId}",
            SafeExecutionTrace.SafeToolName(call.Name), sessionId, admission?.AgentInstanceId, admission?.AgentRunId);
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
        if (admission?.TenantId is not null || (IamToolContracts.IsInspection(call.Name) || call.Name is ToolCatalog.RequestAuthorization or TenantPreferenceToolContract.Name)) return TextResult(Error("forbidden", "Trusted tenant execution is required."));
        if (admission?.AgentInstanceId is { } isolationOwner)
        {
            try { definition = await RestrictExecutionDefinitionAsync(definition, isolationOwner, cancellationToken); }
            catch (AgentCoreException) { return TextResult(Error("forbidden", "Current isolation policy is unavailable.")); }
        }
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

        if (ManagementTools.IsManagement(call.Name) && admission is not null)
            admission = admission with { Harness = await HarnessContextAsync(admission.AgentInstanceId, cancellationToken) };

        var policy = ToolPolicy.EvaluateExecution(definition, call.Name, _configurationGate, approvalGrant, admission);
        if (ConfigurationTools.IsTool(call.Name))
        {
            try { using var decisionArgs = JsonDocument.Parse(call.ArgumentsJson); policy = ManagementDecision(definition, call.Name, decisionArgs.RootElement, admission, approvalGrant, policy); }
            catch (JsonException) { return TextResult(Error("invalid", "Management arguments are malformed.")); }
        }
        if (policy == ToolPolicyDecision.Deny
            || string.IsNullOrWhiteSpace(call.Name))
        {
            if (call.Name == ToolCatalog.EmailSend && emailAccounts is { IsSendEnabled: false })
                return TextResult(Error("smtp_disabled", EmailAccountService.SendSetupGuidance));

            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("forbidden", "Tool is not permitted for this role."));
        }

        if (policy == ToolPolicyDecision.RequireApproval)
        {
            if (call.Name == ToolCatalog.EmailSend && emailAccounts is { IsSendEnabled: false })
                return TextResult(Error("smtp_disabled", EmailAccountService.SendSetupGuidance));

            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("approval_required", "Tool execution requires explicit approval."));
        }

        var payload = ToolArgumentPayload.Normalize(call.ArgumentsJson,
            ToolRegistry.TryGet(call.Name, out var descriptor) ? descriptor.ModelDefinition : null);
        JsonElement args;
        try
        {
            if (string.Equals(call.Name, ToolCatalog.BrowserClose, StringComparison.Ordinal))
            {
                if (!BrowserToolArguments.TryValidateClose(payload, out var closeError))
                {
                    return TextResult(closeError);
                }

                args = JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
            }
            else
            {
                args = JsonSerializer.Deserialize<JsonElement>(
                    payload,
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


        if (LooksLikeSessionMutation(args, call.Name == ToolCatalog.BrowserVisionMouse) || LooksLikeHostPath(args))
        {
            if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
            return TextResult(Error("forbidden", "Tool arguments are not permitted."));
        }

        if (approvalGrant is not null
            && !string.Equals(call.Name, ToolCatalog.EmailSend, StringComparison.Ordinal)
            && !string.Equals(call.Name, ToolCatalog.HttpRequest, StringComparison.Ordinal)
            && !ToolResources.IsTriggerWrite(call.Name))
        {
            var boundHash = ToolActionHash.Compute(call.Name, args);
            if (!string.Equals(boundHash, approvalGrant.ActionHash, StringComparison.Ordinal)
                || !string.Equals(call.Name, approvalGrant.ToolName, StringComparison.Ordinal))
            {
                if (ToolCatalog.IsIdentityMaintenance(call.Name)) RecordMaintenanceRejection(call.Name);
                return TextResult(Error("stale_approval", "Approval no longer matches the requested action."));
            }
        }

        if (InstanceSkillTools.IsManagement(call.Name)) return await ExecuteSkillManagementAsync(definition, call, args, admission, remainingOutputBytes, cancellationToken);
        if (ConfigurationTools.IsTool(call.Name)) return await ExecuteConfigurationAsync(definition, call, args, admission, remainingOutputBytes, cancellationToken);

        try
        {
            if (call.Name == ToolCatalog.AgentStateInspect)
                return await ExecuteStateInspectAsync(sessionId, args, admission, remainingOutputBytes, cancellationToken);
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
                using var source = JsonDocument.Parse(await experience.InspectSourceAsync(ownerId, runId, args, cancellationToken, Math.Min(remainingOutputBytes, 6000)));
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
                var coverage = eventCoverage is null ? null : await eventCoverage.CoveragePageAsync(owner, automationId, args.TryGetProperty("coverageCursor", out var cursor) ? cursor.GetString() : null, 8, cancellationToken);
                var projection = System.Text.Json.Nodes.JsonNode.Parse(TriggerScheduleCommands.RegistrationJson(current))!.AsObject();
                projection["coverage"] = JsonSerializer.SerializeToNode(coverage, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return FitResult(remainingOutputBytes, projection.ToJsonString());
            }
            if (call.Name is ToolCatalog.AutomationEnable or ToolCatalog.AutomationDisable)
            {
                if (admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn }
                    || triggerCommand?.Owner is not AgentCore.Domain.Triggers.TriggerOwner activationOwner
                    || admission.AgentInstanceId != activationOwner.AgentInstanceId)
                    return TextResult(Error("forbidden", "Automation activation requires the current owned user turn."));
                triggerCommand = triggerCommand with { CurrentUserText = admission.OwnerTurnText ?? triggerCommand.CurrentUserText };
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
            if (admission is not null && (call.Name.StartsWith("drive.", StringComparison.Ordinal)
                || call.Name.StartsWith("office.", StringComparison.Ordinal) || call.Name == ToolCatalog.WebDownload))
                admission = admission with { WorkspaceCwd = StateIsolationAccess.Cwd(await IsolationAsync(definition, sessionId, cancellationToken), admission.WorkspaceCwd) };
            if ((call.Name.StartsWith("workspace.", StringComparison.Ordinal) || call.Name == ToolCatalog.ArtifactsCreateFromWorkspace))
            {
                if (!await AgentWorkspaceAvailableAsync(sessionId, cancellationToken))
                    return TextResult(Error("forbidden", "Workspace tools require an Agent Instance."));
                var isolation = await IsolationAsync(definition, sessionId, cancellationToken);
                var cwd = StateIsolationAccess.Cwd(isolation, admission?.WorkspaceCwd);
                if (call.Name == ToolCatalog.WorkspaceCwd)
                    return await ExecuteCwdAsync(definition, sessionId, args, cwd, cancellationToken);
                args = AgentWorkspacePaths.Arguments(call.Name, args, sessionId, cwd);
                CheckWorkspacePolicy(call.Name, args, isolation);
                ProtectCwd(call.Name, args, sessionId, cwd);
            }
            if (call.Name.StartsWith("drive.", StringComparison.Ordinal))
                return FitResult(remainingOutputBytes, await ExecuteDriveAsync(definition, sessionId, call, args, admission, approvalGrant, cancellationToken));
            if (call.Name.StartsWith("office.", StringComparison.Ordinal))
                return FitResult(remainingOutputBytes, await ExecuteOfficeAsync(call.Name, definition, sessionId, args, admission?.WorkspaceCwd ?? "/home", remainingOutputBytes, cancellationToken));
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
                ToolCatalog.SandboxProfiles or ToolCatalog.SandboxExec or ToolCatalog.SandboxStatus or ToolCatalog.SandboxProcessStatus or ToolCatalog.SandboxProcessStop or ToolCatalog.SandboxReset
                    => await ExecuteSandboxToolAsync(definition, sessionId, call, args, admission, remainingOutputBytes, cancellationToken),
                ToolCatalog.WebSearch => FitResult(
                    remainingOutputBytes,
                    await SearchWebAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WebFetch => FitResult(
                    remainingOutputBytes,
                    await FetchWebAsync(args, remainingOutputBytes, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.WebDownload => FitResult(remainingOutputBytes,
                    await DownloadWebAsync(definition, sessionId, args, admission, cancellationToken).ConfigureAwait(false)),
                ToolCatalog.HttpRequest => FitResult(
                    remainingOutputBytes,
                    await ExecuteHttpRequestAsync(args, approvalGrant, cancellationToken).ConfigureAwait(false)),
                _ when ToolCatalog.IsBrowserTool(call.Name) => await ExecuteBrowserAsync(
                    definition, sessionId, call.Name, args, admission, remainingOutputBytes, cancellationToken).ConfigureAwait(false),
                ToolCatalog.DemoSensitiveAction => TextResult(
                    ExecuteDemoSensitiveAction(sessionId, args, approvalGrant)),
                ToolCatalog.EmailRequestAccount => TextResult(await RequestEmailAccountAsync(definition, sessionId, args, admission, cancellationToken)),
                ToolCatalog.EmailAccounts => FitResult(remainingOutputBytes, await ListEmailAccountsAsync(admission, cancellationToken)),
                ToolCatalog.EmailSearch => FitResult(
                    remainingOutputBytes,
                    await SearchEmailAsync(args, remainingOutputBytes, cancellationToken, admission).ConfigureAwait(false)),
                ToolCatalog.EmailRead => FitResult(
                    remainingOutputBytes,
                    await ReadEmailAsync(args, remainingOutputBytes, cancellationToken, admission).ConfigureAwait(false)),
                ToolCatalog.EmailCreateDraft => TextResult(
                    await CreateEmailDraftAsync(args, cancellationToken, admission, sessionId).ConfigureAwait(false)),
                ToolCatalog.EmailSend => TextResult(
                    await SendEmailDraftAsync(args, approvalGrant, cancellationToken, admission, sessionId).ConfigureAwait(false)),
                ToolCatalog.AutomationCreate or ToolCatalog.AutomationList
                    or ToolCatalog.AutomationUpdate or ToolCatalog.AutomationDelete or ToolCatalog.AutomationRun or ToolCatalog.AutomationEnable or ToolCatalog.AutomationDisable => await TriggerScheduleCommands.ExecuteAsync(
                        definition,
                        triggerRegistrations,
                        call.Name,
                        args,
                        triggerCommand,
                        cancellationToken,
                        _triggerAuthorizer,
                        _agentInstances,
                        _agentDefinitions,
                        _profiles, automationAuthoring, approvalGrant).ConfigureAwait(false),
                _ => TextResult(Error("forbidden", "Tool is not permitted for this role."))
            };
            return (call.Name.StartsWith("workspace.", StringComparison.Ordinal) || call.Name is ToolCatalog.ArtifactsCreateFromWorkspace)
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
            var failureId = ex.DiagnosticId ?? HandledFailureLog.Warning(logger, ex, call.Name, ex.Code,
                new DiagnosticContext(SessionId: sessionId, AgentInstanceId: admission?.AgentInstanceId, AgentRunId: admission?.AgentRunId), diagnostics: diagnosticIds);
            return FitResult(remainingOutputBytes, failureId is Guid id
                ? JsonSerializer.Serialize(new { error = ex.Code, message = ex.Message, diagnosticId = id })
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

        if (!record.Readable)
        {
            return TextResult(JsonSerializer.Serialize(new
            {
                attachmentId = record.AttachmentId, displayName = record.DisplayName,
                contentType = record.ContentType, byteSize = record.ByteSize, kind = "binary",
                path = $"/attachments/{record.AttachmentId:D}",
                note = "Original bytes are available for download or authorized workspace.copy. This format has no automatic content reader."
            }));
        }

        if (AttachmentMedia.IsImage(record.ContentType))
        {
            return await ReadHistoricalImageAsync(sessionId, record, remainingOutputBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        var attachmentType = AttachmentMedia.NormalizeContentType(record.ContentType);
        if (attachmentType is OfficeLimits.DocumentMime or OfficeLimits.SpreadsheetMime && officeProcessor is not null)
            return TextResult(await ReadOfficeAttachmentAsync(sessionId, record, args, remainingOutputBytes, cancellationToken));
        if (args.TryGetProperty("sheet", out _) || args.TryGetProperty("range", out _) || args.TryGetProperty("cursor", out _) || args.TryGetProperty("offset", out _) || args.TryGetProperty("limit", out _))
            return TextResult(Error(attachmentType is OfficeLimits.DocumentMime or OfficeLimits.SpreadsheetMime ? "unavailable" : "invalid",
                attachmentType is OfficeLimits.DocumentMime or OfficeLimits.SpreadsheetMime
                    ? "Targeted Office reading is unavailable because the Office processor is not configured."
                    : "Sheet/range and offset/limit are supported only for Office attachments."));
        if (attachmentType is "application/pdf" or OfficeLimits.DocumentMime or OfficeLimits.SpreadsheetMime)
        {
            var kind = attachmentType == "application/pdf" ? "pdf" : "office";
            if (processor is null)
            {
                return TextResult(JsonSerializer.Serialize(new
                {
                    attachmentId = record.AttachmentId,
                    displayName = record.DisplayName,
                    contentType = record.ContentType,
                    byteSize = record.ByteSize,
                    kind,
                    note = "Attachment extraction is unavailable."
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
                    kind,
                    note = "Attachment content could not be extracted. Original bytes remain available."
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
                    kind,
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

        var byteTruncated = buffer.Length < record.ByteSize;
        string text;
        try
        {
            var bytes = buffer.ToArray();
            var decoder = StrictUtf8.GetDecoder();
            var characters = new char[StrictUtf8.GetMaxCharCount(bytes.Length)];
            var count = decoder.GetChars(bytes, 0, bytes.Length, characters, 0, flush: !byteTruncated);
            text = new string(characters, 0, count);
            if (text.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r' or '\f')))
                return TextResult(Error("unsupported_reader", "This content is binary. Original bytes remain available for download or authorized workspace.copy."));
        }
        catch (DecoderFallbackException)
        {
            return TextResult(Error("unsupported_reader", "This content is not valid UTF-8. Original bytes remain available for download or authorized workspace.copy."));
        }
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

        if (args.EnumerateObject().Any(property => property.Name is not ("path" or "cursor" or "limit")))
            return Error("invalid", "List accepts path, cursor and limit.");
        var limit = WorkspaceLimits.MaxListEntries;
        if (args.TryGetProperty("limit", out var limitValue) && (!limitValue.TryGetInt32(out limit) || limit is < 1 or > WorkspaceLimits.MaxListEntries))
            return Error("invalid", "List limit must be between 1 and 256.");
        string? cursor = null;
        if (args.TryGetProperty("cursor", out var cursorValue))
        {
            if (cursorValue.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursorValue.GetString())) return Error("invalid", "Use the nextPath cursor returned by this directory listing.");
            cursor = cursorValue.GetString();
        }
        WorkspaceNodePage page;
        var isolation = await IsolationAsync(definition, sessionId, cancellationToken);
        AgentCore.Application.Agents.StateIsolationAccess.RequireHome(isolation, path, false);
        if (AgentCore.Application.Workspaces.AgentHomePath.IsHome(path) && agentWorkspace is not null)
        {
            await agentWorkspace.RequireSessionHomeAsync(sessionId, path, false, cancellationToken);
            page = await agentWorkspace.ListNodePageAsync(await agentWorkspace.SessionOwnerAsync(sessionId, cancellationToken), path, cursor, limit, cancellationToken);
            var writable = isolation.HomeAccess == HomeAccess.ReadWrite && await agentWorkspace.SessionWritableAsync(sessionId, cancellationToken);
            page = page with { Entries = page.Entries.Select(node => node with { Writable = writable }).ToArray() };
        }
        else page = await workspace.ListPageAsync(sessionId, definition, path, cursor, limit, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            path, page.Truncated, page.NextPath,
            entries = page.Entries.Select(node => new { path = node.LogicalPath, directory = node.Directory,
                kind = node.Directory ? "directory" : node.Kind, byteSize = node.ByteSize, modifiedAt = node.ModifiedAt,
                linkTarget = node.LinkTarget, mode = node.Mode, writable = node.Writable })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private async Task<string> PatchWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (workspace is null
            || !TryString(args, "path", out var path)
            || !args.TryGetProperty("edits", out var editsElement)
            || editsElement.ValueKind != JsonValueKind.Array)
        {
            return Error("invalid", "path and edits are required.");
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
            var patched = await agentWorkspace.PatchAsync(sessionId, path, edits, cancellationToken);
            return JsonSerializer.Serialize(new { path = patched.LogicalPath, byteSize = patched.ByteSize, editsApplied = patched.EditsApplied });
        }
        var result = await workspace.PatchTextAsync(
                sessionId,
                definition,
                path,
                edits,
                cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            path = result.LogicalPath,
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
        if (args.EnumerateObject().Any(p => p.Name is not ("path" or "content")))
            return Error("invalid", "Write accepts path and content.");
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
            var item = await agentWorkspace.WriteAsync(sessionId, path, "text/plain; charset=utf-8", bytes, cancellationToken);
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

        if (!(path == WorkspaceLogicalPath.WorkingDirectory || path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal))
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

    private static bool LooksLikeSessionMutation(JsonElement args, bool allowBrowserSnapshotId = false)
    {
        foreach (var property in args.EnumerateObject())
        {
            // A browser evidence ID is not a mutable Session snapshot. The mouse schema
            // validates this one exact read-only token; every other mutation name stays denied.
            if (allowBrowserSnapshotId && property.Name == "snapshotId") continue;
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

    internal IEmailStore? EmailStore => emailStore;
    internal AgentCore.Application.Credentials.CredentialService? EmailCredentials => credentials;
    private async Task<string> RequestEmailAccountAsync(AgentDefinition definition, Guid sessionId, JsonElement args, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn, AgentInstanceId: { } owner, AgentRunId: { } runId }
            || emailStore is null || emailAccounts is null || !emailAccounts.IsEnabled)
            return JsonSerializer.Serialize(new { error = "needs_attention", destination = "/admin/connections/credentials", message = "Configure an account and Instance binding before starting a new user turn. Unattended work cannot request account selection." });
        if (emailRuns is not null)
        {
            var source = await emailRuns.GetAsync(new(owner, LocalUserProfile.Id), runId, ct);
            if (source is null || source.SessionId != sessionId
                || source.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
                || !AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryReadState(source.Checkpoint, out var messages, out var uncertain, out var blocked)
                || uncertain || blocked is not null || messages is null
                || messages.Any(m => m.Role == ModelRole.Tool && (!ToolRegistry.TryGet(m.Name ?? "", out var descriptor) || descriptor.Effect != ToolEffect.ReadOnly)))
                return JsonSerializer.Serialize(new { error = "needs_attention", destination = "/admin/connections/credentials", message = "Prior effects prevent safe automatic continuation. Configure account access in Admin, then send a new user turn." });
        }
        var target = TryString(args, "target", out var requested) ? requested : ToolCatalog.EmailSearch;
        if (target is not (ToolCatalog.EmailSearch or ToolCatalog.EmailRead or ToolCatalog.EmailCreateDraft or ToolCatalog.EmailSend)
            || !AgentCore.Application.Agents.RolePermissions.AllowsTool(definition, target)) return Error("forbidden", "Requested email operation is not authorized.");
        if (target == ToolCatalog.EmailSend && !emailAccounts.IsSendEnabled)
            return Error("smtp_disabled", EmailAccountService.SendSetupGuidance);
        await emailStore.CreateSelectionAsync(new(runId, owner, sessionId, runId, target, emailAccounts.UtcNow.AddMinutes(10)), ct);
        return JsonSerializer.Serialize(new { status = "account_selection_requested", requestId = runId, message = "Choose an email account in the trusted owner UI, then continue in a fresh Run. Never enter a password in Chat." });
    }
    private static ToolExecutionAdmission RequireMailAdmission(ToolExecutionAdmission? admission) => admission
        ?? throw AgentCoreErrors.Forbidden("Email requires an owned Run admission.");
    private static string? AccountRef(JsonElement args)
    {
        if (!args.TryGetProperty("accountRef", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw AgentCoreErrors.Validation("accountRef must be a bound account alias.");
        try { return AgentCore.Domain.Credentials.CredentialRules.Reference(value.GetString()!); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("accountRef must contain 1–64 letters, digits or hyphens."); }
    }
    private async Task<string> ListEmailAccountsAsync(ToolExecutionAdmission? admission, CancellationToken ct)
    {
        if (emailAccounts is null || !emailAccounts.IsEnabled) return Error("provider_disabled", "Email capability is disabled.");
        var accounts = await emailAccounts.AccountsAsync(RequireMailAdmission(admission), ct);
        return JsonSerializer.Serialize(new { accounts = accounts.Select(c => new { accountRef = c.AccountRef, displayName = c.DisplayName, emailAddress = c.EmailAddress, provider = c.Provider, authentication = c.Authentication, connection = c.Connection, readAvailable = c.ReadAvailable, sendAvailable = c.SendAvailable }), status = accounts.Count == 0 ? "no_eligible_account" : "available", sendAvailable = accounts.Any(c => c.SendAvailable), sendSetupGuidance = !emailAccounts.IsSendEnabled ? EmailAccountService.SendSetupGuidance : accounts.Any(c => c.SendAvailable) ? null : "Connect and bind an account with send permission in Connections → Credentials." });
    }

    public async Task<EmailSendApprovalPrepareResult> PrepareEmailSendApprovalAsync(
        JsonElement args,
        CancellationToken cancellationToken = default, ToolExecutionAdmission? admission = null, Guid sessionId = default)
    {
        if (await LegacyApprovalPreparationErrorAsync(sessionId, admission, cancellationToken).ConfigureAwait(false) is { } denied)
            return new(null, denied);
        if (emailAccounts is null ? emailProvider is null || !emailProvider.IsAvailable : !emailAccounts.IsEnabled)
        {
            return new EmailSendApprovalPrepareResult(null, Error("unavailable", "Email is not configured."));
        }

        if (emailAccounts is { IsSendEnabled: false })
            return new(null, Error("smtp_disabled", EmailAccountService.SendSetupGuidance));

        if (!TryString(args, "draftId", out var draftId))
        {
            return new EmailSendApprovalPrepareResult(null, Error("invalid", "draftId is required."));
        }

        EmailDraftSnapshot? draft;
        try { draft = emailAccounts is null ? await emailProvider!.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
            : await emailAccounts.DraftAsync(sessionId, RequireMailAdmission(admission), draftId, AccountRef(args), cancellationToken);
            if (emailAccounts is not null) emailAccounts.RequireSendEnabled(await emailAccounts.ResolveAsync(RequireMailAdmission(admission), draft!.AccountRef, cancellationToken)); }
        catch (AgentCoreException ex) { return new(null, Error(ex.Code, ex.Message)); }
        if (draft is null)
        {
            return new EmailSendApprovalPrepareResult(null, Error("notFound", "Draft was not found."));
        }

        var normalized = EmailDraftNormalizer.Normalize(draft);
        var actionHash = EmailDraftNormalizer.ComputeSendActionHash(normalized);
        var summary = ToolApprovalPreview.BoundSummary($"Send email to {string.Join(", ", normalized.To)}");
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["To"] = string.Join(", ", normalized.To),
            ["Cc"] = string.Join(", ", normalized.Cc),
            ["Bcc"] = string.Join(", ", normalized.Bcc),
            ["Subject"] = normalized.Subject,
            ["Body"] = normalized.Body
        };
        if (normalized.AccountId is not null)
        {
            details["Account"] = $"{normalized.AccountRef} · {normalized.AccountId:D}";
            details["From"] = normalized.Sender ?? "";
            details["Revisions"] = $"Account {normalized.AccountRevision}; draft {normalized.Revision}; binding {normalized.BindingRevision}";
        }
        return new EmailSendApprovalPrepareResult(
            new EmailSendApprovalPreparation(actionHash, summary, details),
            null);
    }

    private async Task<string> SearchEmailAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken, ToolExecutionAdmission? admission = null)
    {
        if (emailAccounts is null ? emailProvider is null || !emailProvider.IsAvailable : !emailAccounts.IsEnabled)
        {
            return Error("unavailable", "Email is not configured.");
        }

        var query = "";
        if (args.TryGetProperty("query", out var queryProperty))
        {
            if (queryProperty.ValueKind != JsonValueKind.String) return Error("invalid", "query must be a string.");
            query = queryProperty.GetString()!;
        }

        if (query.Length > EmailToolLimits.MaxSearchQueryLength)
        {
            return Error("invalid", $"query must be at most {EmailToolLimits.MaxSearchQueryLength} characters.");
        }
        query = query.Trim();

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
        var scopedAccount = emailAccounts is null ? null : await emailAccounts.ResolveAsync(RequireMailAdmission(admission), AccountRef(args), cancellationToken);
        var selectedProvider = scopedAccount is null ? emailProvider! : emailAccounts!.Provider(scopedAccount);
        var result = await selectedProvider
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
            JsonSerializer.Serialize(new { accountRef = scopedAccount?.AccountRef, query, results = items, truncated = result.Truncated }));
    }

    private async Task<string> ReadEmailAsync(
        JsonElement args,
        int remainingOutputBytes,
        CancellationToken cancellationToken, ToolExecutionAdmission? admission = null)
    {
        if (emailAccounts is null ? emailProvider is null || !emailProvider.IsAvailable : !emailAccounts.IsEnabled)
        {
            return Error("unavailable", "Email is not configured.");
        }

        if (!TryString(args, "messageId", out var messageId))
        {
            return Error("invalid", "messageId is required.");
        }

        var started = Stopwatch.GetTimestamp();
        var scopedAccount = emailAccounts is null ? null : await emailAccounts.ResolveAsync(RequireMailAdmission(admission), AccountRef(args), cancellationToken);
        var selectedProvider = scopedAccount is null ? emailProvider! : emailAccounts!.Provider(scopedAccount);
        var message = await selectedProvider
            .ReadAsync(new EmailReadRequest(messageId), cancellationToken)
            .ConfigureAwait(false);
        RuntimeTelemetry.Record("email.read", RuntimeTelemetry.ElapsedMs(started));
        return ToolJsonResults.FitJsonWithContentField(
            remainingOutputBytes,
            message.Body,
            (body, truncated) => JsonSerializer.Serialize(new
            {
                accountRef = scopedAccount?.AccountRef,
                trust = "untrusted_email_content",
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

    private async Task<string> CreateEmailDraftAsync(JsonElement args, CancellationToken cancellationToken, ToolExecutionAdmission? admission = null, Guid sessionId = default)
    {
        if (emailAccounts is null ? emailProvider is null || !emailProvider.IsAvailable : !emailAccounts.IsEnabled)
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
        var created = emailAccounts is null
            ? await emailProvider!.CreateDraftAsync(new EmailCreateDraftRequest(to, cc, bcc, subject, body), cancellationToken).ConfigureAwait(false)
            : new EmailDraftResult(await emailAccounts.CreateDraftAsync(sessionId, RequireMailAdmission(admission), AccountRef(args), new(to, cc, bcc, subject, body), cancellationToken));
        RuntimeTelemetry.Record("email.create_draft", RuntimeTelemetry.ElapsedMs(started));
        return JsonSerializer.Serialize(new
        {
            storage = emailAccounts is null ? "provider" : "core",
            accountRef = created.Draft.AccountRef,
            sender = created.Draft.Sender,
            body = created.Draft.Body,
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
        CancellationToken cancellationToken, ToolExecutionAdmission? admission = null, Guid sessionId = default)
    {
        if (approvalGrant is null)
        {
            return Error("approval_required", "Tool execution requires explicit approval.");
        }

        if (emailAccounts is null ? emailProvider is null || !emailProvider.IsAvailable : !emailAccounts.IsEnabled)
        {
            return Error("unavailable", "Email is not configured.");
        }

        if (!TryString(args, "draftId", out var draftId))
        {
            return Error("invalid", "draftId is required.");
        }

        if (emailAccounts is not null)
        {
            var receipt = await emailAccounts.SendAsync(sessionId, RequireMailAdmission(admission), draftId, AccountRef(args), approvalGrant, cancellationToken);
            return JsonSerializer.Serialize(new { outcome = receipt.Outcome.ToString().ToLowerInvariant(), providerMessageId = receipt.ProviderMessageId, error = receipt.ErrorCode, message = receipt.ErrorMessage });
        }
        var draft = await emailProvider!.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
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
