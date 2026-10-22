using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantManagementMutationRules
{
    public const string Completed = "management_mutation_completed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void Eligible(TenantManagementReader reader, string tool)
    {
        TenantSessionRules.RequireExecutor(reader.Security, reader.Agent);
        if (reader.Root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage
            || reader.Root.Admission.OutputContract != AgentRunOutputContract.ConversationResponse || reader.Root.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation)
            throw new AccessDeniedException("management_unavailable");
        var current = reader.Current with { SelfManagement = ManagementPolicyResolver.Resolve(reader.Baseline, reader.Agent.Instance) };
        if (!TenantManagementReadContract.Allows(reader.Run) || reader.Run.Admission.Configuration is null
            || !TenantManagementMutationContract.Allows(reader.Run.Admission.Configuration.Definition, tool)
            || !TenantManagementMutationContract.Allows(current, tool)) throw new AccessDeniedException("management_unavailable");
    }
    public static PreparedManagementMutation Build(TenantManagementSource source, TenantManagementMutationArguments args,
        TenantAgentConfigurationChange change, DateTimeOffset at)
        => Build(source.Reader, args, change, at);
    private static PreparedManagementMutation Build(TenantManagementReader reader, TenantManagementMutationArguments args,
        TenantAgentConfigurationChange change, DateTimeOffset at)
    {
        var owner = reader.Agent.Instance; Eligible(reader, args.Tool);
        if (args.ExpectedInstanceRevision != owner.Revision || change.ExpectedInstanceRevision != owner.Revision || change.InstanceId != owner.InstanceId)
            throw new SecurityConflictException("stale_revision");
        var after = TenantAgentConfigurationRules.Apply(owner, change, at);
        var rows = new List<TenantApprovalFieldChange>();
        if (args.Tool == "settings.patch")
        {
            var section = args.Value.GetProperty("section").GetString()!;
            var beforeView = AgentInstanceSettingsService.View(owner, reader.Baseline, section);
            var afterView = AgentInstanceSettingsService.View(after, reader.Baseline, section);
            var fields = args.Value.GetProperty("set").EnumerateObject().Select(p => p.Name).Concat(args.Value.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!)).Order(StringComparer.Ordinal).ToArray();
            foreach (var field in fields)
                rows.Add(new(field, beforeView.Effective[field], afterView.Effective[field], RestoresDefault: args.Value.GetProperty("clear").EnumerateArray().Any(p => p.GetString() == field)));
            rows.Add(new("Field sources", string.Join("\n", fields.Select(field => field + ": " + beforeView.Sources[field])),
                string.Join("\n", fields.Select(field => field + ": " + afterView.Sources[field]))));
            if (change.AutomationPolicyChanges is { Count: > 0 } plans)
                rows.Add(new("Automation reconciliation", "Existing registrations retain their own ownership and history", "Reconcile scheduling eligibility with the exact observed registration revisions. This grants no future execution authority."));
        }
        else if (args.Tool == "automation.preferences.patch")
        {
            rows.Add(new("Unattended model", owner.UnattendedModelCatalogKey ?? "Inherit Agent default", after.UnattendedModelCatalogKey ?? "Inherit Agent default", RestoresDefault: after.UnattendedModelCatalogKey is null));
            rows.Add(new("Unattended reasoning", owner.UnattendedReasoningEffort ?? "Inherit Agent default", after.UnattendedReasoningEffort ?? "Inherit Agent default", RestoresDefault: after.UnattendedReasoningEffort is null));
        }
        else
        {
            var beforePolicy = SelfManagementService.View(owner, reader.Baseline); var afterPolicy = SelfManagementService.View(after, reader.Baseline);
            rows.Add(new("Management mode", beforePolicy.Effective.Mode.ToString(), afterPolicy.Effective.Mode.ToString()));
            rows.Add(new("Management scopes", Choices(beforePolicy.Effective.Scopes), Choices(afterPolicy.Effective.Scopes)));
            rows.Add(new("Managed allowances", Choices(beforePolicy.Effective.ManagedAutoApply ?? []), Choices(afterPolicy.Effective.ManagedAutoApply ?? [])));
            rows.Add(new("Frozen", beforePolicy.Effective.Frozen, afterPolicy.Effective.Frozen));
        }
        rows.Add(new("Effect boundary", "Current Run configuration stays pinned", args.Tool == "management.freeze"
            ? "Management revokes immediately. Ordinary tasks and retained content remain; only an authorized person can unfreeze."
            : args.Tool == "management.policy.update" ? "New grants apply to the next new Run; authority removals revoke immediately. No IAM is granted."
            : "Changes apply to the next new Run. No task effect is replayed."));
        var changes = JsonSerializer.Serialize(rows, Json);
        DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", Encoding.UTF8.GetBytes(changes));
        var result = new PreparedManagementMutation(reader.Run.SessionId, reader.Root.SessionId, args.Tool, args.Json,
            TenantManagementReadRules.ConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(after), changes,
            change.SettingsOverrides, change.HarnessManagement, change.AutomationPolicyChanges, change.UnattendedModelCatalogKey, change.UnattendedReasoningEffort);
        Validate(result); return result;
    }
    public static void Validate(PreparedManagementMutation value)
    {
        var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson);
        if (args.Json != value.ArgumentsJson || value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty
            || !TenantMemoryOperationRules.ValidHash(value.ConfigurationHash) || !TenantMemoryOperationRules.ValidHash(value.BeforeStableHash) || !TenantMemoryOperationRules.ValidHash(value.AfterStableHash)
            || Encoding.UTF8.GetByteCount(value.ChangesJson) > 48000 || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value)) > 98000
            || value.ToolName == "settings.patch" && (value.Settings is null || value.AutomationChanges is null || value.Management is not null || value.UnattendedModelCatalogKey is not null || value.UnattendedReasoningEffort is not null)
            || value.ToolName is "management.freeze" or "management.policy.update" && (value.Management is null || value.Settings is not null || value.AutomationChanges is not null || value.UnattendedModelCatalogKey is not null || value.UnattendedReasoningEffort is not null)
            || value.ToolName == "automation.preferences.patch" && (value.Settings is not null || value.Management is not null || value.AutomationChanges is not null))
            throw new ArgumentException("Use one exact bounded management plan.");
        using var changes = JsonDocument.Parse(value.ChangesJson); if (changes.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("Use exact saved change rows.");
    }
    public static TenantAgentConfigurationChange Change(TenantManagementReader reader, PreparedManagementMutation value)
    {
        Validate(value); Eligible(reader, value.ToolName);
        var owner = reader.Agent.Instance; var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson);
        if (args.ExpectedInstanceRevision != owner.Revision || value.SessionId != reader.Run.SessionId || value.RootSessionId != reader.Root.SessionId
            || value.ConfigurationHash != TenantManagementReadRules.ConfigurationHash(owner) || value.BeforeStableHash != TenantManagementReadRules.StableConfigurationHash(owner))
            throw new AccessDeniedException("management_source_changed");
        TenantAgentConfigurationChange change;
        if (value.ToolName == "settings.patch")
        {
            var settings = InstanceSettingsResolver.Patch(owner.SettingsOverrides ?? new(), args.Value.GetProperty("section").GetString()!,
                args.Value.GetProperty("set").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), args.Value.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!).ToArray());
            if (JsonSerializer.Serialize(settings) != JsonSerializer.Serialize(value.Settings)) throw new AccessDeniedException("management_source_changed");
            change = new(owner.InstanceId, owner.Revision, SetSettingsOverrides: true, SettingsOverrides: settings, AutomationPolicyChanges: value.AutomationChanges);
        }
        else if (value.ToolName == "automation.preferences.patch")
        {
            if (value.UnattendedModelCatalogKey != args.Value.GetProperty("catalogKey").GetString() || value.UnattendedReasoningEffort != args.Value.GetProperty("reasoningEffort").GetString())
                throw new AccessDeniedException("management_source_changed");
            change = new(owner.InstanceId, owner.Revision, SetUnattendedModel: true, UnattendedModelCatalogKey: value.UnattendedModelCatalogKey, UnattendedReasoningEffort: value.UnattendedReasoningEffort);
        }
        else
        {
            var policy = value.ToolName == "management.freeze" ? SelfManagementService.PrepareFreeze(owner, reader.Baseline) : Policy(owner, reader.Baseline, args.Value);
            if (JsonSerializer.Serialize(policy) != JsonSerializer.Serialize(value.Management)) throw new AccessDeniedException("management_source_changed");
            change = new(owner.InstanceId, owner.Revision, HarnessManagement: policy);
        }
        var after = TenantAgentConfigurationRules.Apply(owner, change, DateTimeOffset.UnixEpoch);
        if (TenantManagementReadRules.StableConfigurationHash(after) != value.AfterStableHash) throw new AccessDeniedException("management_source_changed");
        var rebuilt = Build(reader, args, change, DateTimeOffset.UnixEpoch);
        if (!Matches(value, rebuilt)) throw new AccessDeniedException("management_source_changed");
        return change;
    }
    private static string Choices<T>(IEnumerable<T> values)
    {
        var text = string.Join(", ", values);
        return text.Length == 0 ? "None" : text;
    }
    private static HarnessManagementState Policy(AgentInstance owner, AgentDefinition baseline, JsonElement args)
    {
        var view = SelfManagementService.View(owner, baseline); var overrides = view.Overrides ?? (view.Source == "instance" ? ManagementPolicyOverrides.FromLegacy(view.Effective) : new());
        overrides = overrides with { Mode = new(Enum.Parse<HarnessManagementMode>(args.GetProperty("mode").GetString()!, false)),
            Scopes = new(args.GetProperty("scopes").EnumerateArray().Select(a => Enum.Parse<HarnessManagementScope>(a.GetString()!, false)).ToArray()),
            AutoApply = new(args.GetProperty("managedAutoApply").EnumerateArray().Select(a => Enum.Parse<ManagementAutoApply>(a.GetString()!, false)).ToArray()) };
        return SelfManagementService.Prepare(owner, baseline, overrides, null, agent: true);
    }
    public static async ValueTask PrimaryAsync(TenantApprovalRequest request, TenantManagementReader reader, IamAuthorizer iam, CancellationToken ct)
    {
        if (request.Kind == ApprovalKind.Review && !(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId, PermissionCatalog.Get("agents.update"), TenantManagementReadRules.Scope(reader), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("operation_authority_changed");
    }
    public static async ValueTask SecondaryAsync(TenantManagementReader reader, PreparedManagementMutation value, IamAuthorizer iam, Guid actor, CancellationToken ct)
    {
        var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson).Value;
        if (value.ToolName != "settings.patch" || args.GetProperty("section").GetString() != "sandboxPolicy") return;
        foreach (var key in new[] { "sandbox.read", "sandbox.manage" })
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get(key), TenantManagementReadRules.Scope(reader), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("sandbox_configuration_required");
        foreach (var id in InstanceSettingsResolver.Resolve(reader.Baseline, value.Settings).SandboxPolicy?.AllowedProfileIds ?? [])
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get("sandbox.read"), TenantSandboxProfileRules.Scope(reader.Security.Tenant.TenantId, id), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("sandbox_configuration_required");
        if (reader.Security.Users.Any(u => u.PrincipalId == actor)
            && TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get("sandbox.manage")).IndependentReview
            && reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId == actor) throw new AccessDeniedException("independent_reviewer_required");
    }
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantManagementReader reader, PreparedManagementMutation value,
        Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!reader.Security.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await TenantManagementReadRules.RawAsync(reader, iam, ct, human).ConfigureAwait(false);
        var permission = PermissionCatalog.Get("agents.update"); var state = reader.Security;
        foreach (var key in request.Kind == ApprovalKind.ExceptionalGrant ? new[] { "agents.update", "approvals.approve", "approvals.grant_exception" } : new[] { "agents.update", "approvals.approve" })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), TenantManagementReadRules.Scope(reader), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        if (TenantOperationPolicyService.Effective(state, permission).IndependentReview && request.OriginalInitiatorPrincipalId == human)
            throw new AccessDeniedException("independent_reviewer_required");
        await SecondaryAsync(reader, value, iam, human, ct).ConfigureAwait(false);
    }
    public static bool Matches(PreparedManagementMutation expected, PreparedManagementMutation current) => JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(current);
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.ManagementMutation is null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Completed) throw new SecurityConflictException("operation_mismatch");
    }
}
