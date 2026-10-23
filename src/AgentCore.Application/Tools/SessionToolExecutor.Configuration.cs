using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly JsonSerializerOptions ManagementJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static bool OwnerOnlySandboxFields(string name, JsonElement args) =>
        name is "settings.patch" or "configuration.propose" && args.GetProperty("section").GetString() == "sandboxPolicy"
        && args.GetProperty("set").EnumerateObject().Select(p => p.Name).Concat(args.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!)).Any(f => f != "profileId");

    private static InstanceSettingsSection ModelSettings(InstanceSettingsSection view) => view.Section != "sandboxPolicy" ? view : view with {
        Overrides = view.Overrides.Where(p => p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value),
        Effective = view.Effective.Where(p => p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value),
        Sources = view.Sources.Where(p => p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value),
        DefinitionDefaults = view.DefinitionDefaults.Where(p => p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value),
        Constraints = view.Constraints.Where(p => p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value)
    };

    private ToolPolicyDecision ManagementDecision(AgentDefinition definition, string name, JsonElement args,
        ToolExecutionAdmission? admission, ToolPolicyDecision fallback)
    {
        if (!ConfigurationTools.IsTool(name) || fallback == ToolPolicyDecision.Deny) return fallback;
        try
        {
            ConfigurationTools.Validate(name, args);
            if (OwnerOnlySandboxFields(name, args)) return ToolPolicyDecision.Deny;
            // Core permits only these specific local preferences; max tokens, instructions and every authority field retain approval.
            if (name is "settings.patch" or "configuration.propose" && args.GetProperty("section").GetString() == "conversationPolicy"
                && args.GetProperty("set").ValueKind == JsonValueKind.Object && args.GetProperty("clear").ValueKind == JsonValueKind.Array
                && args.GetProperty("set").EnumerateObject().Select(p => p.Name).Concat(args.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!))
                    .All(f => f is "responseLength" or "language" or "askOneQuestionAtATime")
                && AutoApply(definition, admission, ManagementAutoApply.ConversationPreferences)) return ToolPolicyDecision.Allow;
            if (name == "resources.upsert" && !args.TryGetProperty("key", out _)
                && AutoApply(definition, admission, ManagementAutoApply.TextResources)) return ToolPolicyDecision.Allow;
            return fallback;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return ToolPolicyDecision.Deny; }
    }

    private static bool AutoApply(AgentDefinition definition, ToolExecutionAdmission? admission, ManagementAutoApply operation) =>
        admission?.Harness?.Policy.AutoApplies(operation) == true && (definition.SelfManagement?.AutoApplies(operation) ?? true);

    private async ValueTask<object> InspectConfigurationAsync(Guid id, AgentDefinition admitted, JsonElement args, ToolExecutionAdmission admission, CancellationToken ct)
    {
        var policy = await selfManagement!.ReadAsync(id, ct);
        var state = policy.Effective;
        var goalScoped = args.TryGetProperty("goal", out _) || args.TryGetProperty("target", out _);
        var diagnosedTarget = DiagnosisTarget(args);
        var settingsViews = state.Allows(HarnessManagementScope.Settings) && admitted.SelfManagement?.Allows(HarnessManagementScope.Settings) != false && instanceSettings is not null
            ? (await instanceSettings.ReadAllAsync(id, ct)).Select(ModelSettings).ToArray() : [];
        if (goalScoped && diagnosedTarget is null) settingsViews = [];
        else if (goalScoped) settingsViews = settingsViews.Where(s => s.Section == (diagnosedTarget == "settings.patch" ? "conversationPolicy" : "capabilities")).ToArray();
        object? blocker = null;
        if (diagnosedTarget is { } name)
        {
            var registered = ToolRegistry.TryGet(name, out var descriptor);
            var baseline = _agentDefinitions is null ? admitted : await _agentDefinitions.GetAsync(admitted.Id, admitted.Version, ct) ?? admitted;
            var authorized = registered && descriptor!.DefinitionAuthorizable && RolePermissions.AllowsTool(baseline, name);
            var enabled = authorized && RolePermissions.AllowsTool(admitted, name);
            var configured = _configurationGate.IsConfigured(name);
            var hostReason = name.StartsWith("email.", StringComparison.Ordinal) ? "provider_unavailable" : "host_only";
            var reasons = new List<string>();
            if (!authorized) reasons.Add("definition_limit");
            else { if (!enabled) reasons.Add("instance_selection"); if (!configured) reasons.Add(hostReason); }
            blocker = new { target = name, outcome = !authorized || !configured ? "owner_action_required" : !enabled ? "instance_restriction" : "ready",
                reason = !authorized ? "definition_limit" : !configured ? hostReason : !enabled ? "instance_selection" : "available", reasons,
                nextStep = !authorized ? "Review and publish the selected Definition in Admin; an Instance cannot exceed its authority."
                    : !configured ? "Configure the integration in Admin or host setup before requesting any Instance remedy."
                    : !enabled ? "Review the returned exact capability-selection remedy and request approval."
                    : "Use the ordinary tool with its own policy and approvals.",
                adminPath = "/admin/instances/" + id.ToString("D") + "/identity/settings" };
        }
        else if (goalScoped) blocker = new { outcome = "owner_action_required", reason = "unsupported_goal",
            nextStep = "Name the actual blocked tool or inspect the relevant area. Core cannot infer a safe setting change from this goal.",
            adminPath = "/admin/instances/" + id.ToString("D") + "/identity/settings" };
        var connectionGoal = !goalScoped || diagnosedTarget is ToolCatalog.BrowserFillCredential || args.TryGetProperty("goal", out var connectionText) && connectionText.GetString()!.Contains("sign in", StringComparison.OrdinalIgnoreCase);
        var connectionsGranted = state.Allows(HarnessManagementScope.Connections) && admitted.SelfManagement?.Allows(HarnessManagementScope.Connections) != false && credentials is not null;
        var bindings = connectionGoal && connectionsGranted
            ? await credentials!.SafeMetadataAsync(id, ct) : [];
        var eligible = connectionGoal && connectionsGranted
            ? (await credentials!.ListAsync(ct)).Where(c => c.Status == "Active").Take(24)
                .Select(c => new { c.CredentialId, c.DisplayName, c.Kind }).ToArray() : [];
        if (diagnosedTarget == ToolCatalog.BrowserFillCredential && RolePermissions.AllowsTool(admitted, diagnosedTarget)
            && _configurationGate.IsConfigured(diagnosedTarget) && connectionsGranted && !bindings.Any(b => b.Kind == "Password"))
            blocker = new { target = diagnosedTarget, outcome = "owner_action_required", reason = "credential_binding_missing",
                nextStep = "Choose an eligible existing System Credential and propose its exact Instance binding, or configure sign-in in Admin. OAuth and MFA require owner interaction.",
                adminPath = "/admin/instances/" + id.ToString("D") + "/credentials" };
        var skillViews = !goalScoped && state.Allows(HarnessManagementScope.Skills) && admitted.SelfManagement?.Allows(HarnessManagementScope.Skills) != false && instanceSkills is not null
            ? (await instanceSkills.ListAsync(id, admitted, ct)).Take(24).Select(s => new { s.Key, s.Name, s.Enabled, s.Revision, s.MissingCapabilities }).ToArray() : [];
        var resourceViews = !goalScoped && state.Allows(HarnessManagementScope.Resources) && admitted.SelfManagement?.Allows(HarnessManagementScope.Resources) != false && instanceResources is not null
            ? (await instanceResources.ListAsync(id, ct)).Resources.Take(24).ToArray() : [];
        var remedy = goalScoped ? await DiagnoseRemedyAsync(id, policy, admitted, args, ct) : null;
        var options = remedy is null ? Array.Empty<ConfigurationRemedy>() : new[] { remedy };
        return new { outcome = "inspected", instanceRevision = policy.InstanceRevision, policyRevision = policy.PolicyRevision,
            changesApplyTo = "nextRun", settings = settingsViews, skills = skillViews, resources = resourceViews,
            bindings, eligibleCredentials = eligible, blocker,
            remediationOptions = options,
            continuation = "After a diagnosed committed change Core may admit one bounded fresh Run for the unfinished task. New writes require new exact approval; uncertain effects never replay.",
            ownerOnly = "Provider secrets, OAuth/MFA, host policies and Definition-denied authority require owner setup in Admin." };
    }

    private async Task<ToolExecutionResult> ExecuteConfigurationAsync(AgentDefinition definition, ModelToolCall call, JsonElement args,
        ToolExecutionAdmission? admission, int budget, CancellationToken ct)
    {
        if (admission is not { AgentInstanceId: Guid id, Detached: false, TriggerKind: TriggerKind.UserTurn } || selfManagement is null)
            return TextResult(Error("forbidden", "Trusted attached owner context is required."));
        try
        {
            ConfigurationTools.Validate(call.Name, args);
            if (OwnerOnlySandboxFields(call.Name, args)) throw AgentCoreErrors.Forbidden("Sandbox initialization and limit changes are managed in the owner Admin interface.");
            var policy = await selfManagement.ReadAsync(id, ct);
            // A current revision binds every mutation to the policy/configuration reviewed before approval.
            if (ToolRegistry.Get(call.Name).Effect != ToolEffect.ReadOnly && args.GetProperty("expectedInstanceRevision").GetInt64() != policy.InstanceRevision)
                throw AgentCoreErrors.Conflict("Instance revision changed. Inspect and request a fresh exact action.");
            object result;
            var revision = policy.InstanceRevision;
            switch (call.Name)
            {
                case "configuration.inspect": result = await InspectConfigurationAsync(id, definition, args, admission, ct); break;
                case "management.inspect": result = policy; break;
                case "automation.preferences.inspect":
                    var automationOwner = await _agentInstances!.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Instance was not found.");
                    result = new { instanceRevision = revision, automationOwner.UnattendedModelCatalogKey, automationOwner.UnattendedReasoningEffort,
                        triggerPolicy = await instanceSettings!.ReadAsync(id, "triggerPolicy", ct), changesApplyTo = "nextRun" }; break;
                case "automation.preferences.patch":
                    if (instanceAdmin is null) throw AgentCoreErrors.Validation("Automation model preferences are unavailable in this host.");
                    var automationUpdated = await instanceAdmin.SetUnattendedModelAsync(id, revision,
                        args.GetProperty("catalogKey").GetString(), args.GetProperty("reasoningEffort").GetString(), ct, AdminEventActorKind.Agent);
                    result = new { outcome = automationUpdated.Revision == revision ? "unchanged" : "applied", instanceRevision = automationUpdated.Revision,
                        automationUpdated.UnattendedModelCatalogKey, automationUpdated.UnattendedReasoningEffort, changesApplyTo = "nextRun" }; break;
                case "management.freeze": result = new { outcome = "frozen", policy = await selfManagement.FreezeAsync(id, revision, ct), changesApplyTo = "immediateRevocation", ordinaryTasksAvailable = true }; break;
                case "management.policy.update":
                    var overrides = policy.Overrides ?? (policy.Source == "instance"
                        ? ManagementPolicyOverrides.FromLegacy(policy.Effective) : new ManagementPolicyOverrides());
                    overrides = overrides with {
                        Mode = new(Enum.Parse<HarnessManagementMode>(args.GetProperty("mode").GetString()!, false)),
                        Scopes = new(args.GetProperty("scopes").EnumerateArray().Select(a => Enum.Parse<HarnessManagementScope>(a.GetString()!, false)).ToArray()),
                        AutoApply = new(args.GetProperty("managedAutoApply").EnumerateArray().Select(a => Enum.Parse<ManagementAutoApply>(a.GetString()!, false)).ToArray()) };
                    result = new { outcome = "applied", policy = await selfManagement.UpdateAsync(id, revision, overrides, agent: true, ct: ct), changesApplyTo = "nextRun", revocationsApplyTo = "immediate" }; break;
                case "settings.list": result = (await instanceSettings!.ReadAllAsync(id, ct)).Select(ModelSettings).ToArray(); break;
                case "settings.inspect":
                    result = ModelSettings(await instanceSettings!.ReadAsync(id, args.GetProperty("section").GetString()!, ct)); break;
                case "configuration.propose":
                case "settings.patch":
                    if (!policy.Effective.Allows(HarnessManagementScope.Settings) || definition.SelfManagement?.Allows(HarnessManagementScope.Settings) == false)
                        throw AgentCoreErrors.Forbidden("Settings are outside the effective management grant.");
                    if (call.Name == "configuration.propose") await ValidateTargetedRemedyAsync(id, policy, definition, args, ct);
                    var section = args.GetProperty("section").GetString()!;
                    var set = args.GetProperty("set").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                    var clear = args.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!).ToArray();
                    var updated = await instanceSettings!.PatchAsync(id, section, revision, set, clear, ct, AdminEventActorKind.Agent);
                    result = new { outcome = updated.InstanceRevision == revision ? "unchanged" : "applied", state = ModelSettings(updated), changesApplyTo = "nextRun",
                        pendingTask = call.Name == "configuration.propose" && args.TryGetProperty("target", out var taskTarget)
                            && admission.AgentRunId is { } sourceRun && args.TryGetProperty("goal", out var taskGoal)
                            ? new AgentCore.Application.Execution.ConfigurationPendingTask(sourceRun, taskTarget.GetString()!, taskGoal.GetString()!, updated.InstanceRevision) : null,
                        continuation = "Core may admit a bounded fresh Run for a diagnosed task. Other tasks require a deliberate next owner turn; never replay uncertain effects." }; break;
                case "resources.list": result = await instanceResources!.ListAsync(id, ct); break;
                case "resources.inspect":
                    var read = await instanceResources!.ReadContentAsync(id, args.GetProperty("key").GetString()!, ct);
                    result = new { resource = read.Resource, content = DefinitionResourcePolicies.IsTextualKnowledgeMediaType(read.Resource.MediaType)
                        ? ToolJsonResults.ClipUtf8Prefix(Encoding.UTF8.GetString(read.Bytes), 32000) : null, truncated = read.Bytes.Length > 32000 }; break;
                case "resources.upsert":
                    var source = args.GetProperty("source").GetString()!;
                    if (!(source == "conversation:user" && !string.IsNullOrWhiteSpace(admission.OwnerTurnText))
                        && !(admission.HarnessSources ?? []).Any(s => s.Source == source))
                        throw AgentCoreErrors.Validation("Read the source through an authorized tool this Run, or use current owner-provided material.");
                    Guid? resourceId = args.TryGetProperty("key", out var key) ? AgentInstanceResourceService.ParseKey(key.GetString()!).Id : null;
                    if (key.ValueKind != JsonValueKind.Undefined && !key.GetString()!.StartsWith("instance:", StringComparison.Ordinal))
                        throw AgentCoreErrors.Forbidden("Inherited bytes are read-only.");
                    result = await instanceResources!.UpsertAsync(id, revision, resourceId,
                        args.TryGetProperty("expectedResourceRevision", out var resourceRevision) ? resourceRevision.GetInt64() : null,
                        args.GetProperty("logicalPath").GetString()!, Enum.Parse<AgentDefinitionResourceKind>(args.GetProperty("kind").GetString()!),
                        args.GetProperty("mediaType").GetString()!, Encoding.UTF8.GetBytes(args.GetProperty("content").GetString()!), actor: SkillAuthor.Agent, ct: ct); break;
                case "resources.set_enabled": result = await instanceResources!.SetEnabledAsync(id, args.GetProperty("key").GetString()!, revision,
                    args.GetProperty("expectedResourceRevision").GetInt64(), args.GetProperty("enabled").ValueKind == JsonValueKind.Null ? null : args.GetProperty("enabled").GetBoolean(), SkillAuthor.Agent, ct); break;
                case "resources.delete": result = await instanceResources!.DeleteAsync(id, args.GetProperty("key").GetString()!, revision,
                    args.GetProperty("expectedResourceRevision").GetInt64(), SkillAuthor.Agent, ct); break;
                case "resources.copy": result = await instanceResources!.CopyAsync(id, args.GetProperty("key").GetString()!, revision, args.GetProperty("logicalPath").GetString()!, ct, SkillAuthor.Agent); break;
                case "credentials.binding.propose":
                    var credentialId = Guid.Parse(args.GetProperty("credentialId").GetString()!);
                    var available = await credentials!.GetAsync(credentialId, ct);
                    if (available.Status != "Active") throw AgentCoreErrors.Forbidden("This credential is unavailable; owner setup is required.");
                    var bound = await credentials.BindAsync(id, credentialId, args.GetProperty("reference").GetString()!, revision, ct, AdminEventActorKind.Agent);
                    result = new { outcome = "applied", bound.Reference, credential = new { bound.Credential.DisplayName, bound.Credential.Kind }, changesApplyTo = "nextRun", requiresSeparateTaskApproval = true }; break;
                case "credentials.binding.remove":
                    await credentials!.UnbindAsync(id, Guid.Parse(args.GetProperty("bindingId").GetString()!), args.GetProperty("expectedBindingRevision").GetInt64(), revision, ct, AdminEventActorKind.Agent);
                    result = new { outcome = "applied", changesApplyTo = "immediateRevocation", requiresSeparateTaskApproval = true }; break;
                default: throw AgentCoreErrors.Validation("Unsupported management operation; use Admin.");
            }
            return FitResult(budget, JsonSerializer.Serialize(result, ManagementJson));
        }
        catch (AgentCoreException e) { return TextResult(Error(e.Code, e.Message)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        { return TextResult(Error("invalid", "Management arguments do not match the typed schema. Inspect and correct them.")); }
    }
}
