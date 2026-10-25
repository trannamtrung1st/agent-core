using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;

public static class TenantManagementReadRules
{
    public const string Completed = "management_read_completed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static AccessScope Scope(TenantManagementReader reader) => AccessScope.ForResource(reader.Security.Tenant.TenantId, "agent", reader.Run.AgentInstanceId.ToString("D"));
    public static void Eligible(TenantManagementReader reader, AgentDefinition baseline, string tool)
    {
        if (tool == ToolCatalog.SkillsLoad) { TenantSkillLoadRules.Eligible(reader); return; }
        TenantSessionRules.RequireExecutor(reader.Security, reader.Agent);
        if (reader.Root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage
            || reader.Root.Admission.OutputContract != AgentRunOutputContract.ConversationResponse || reader.Root.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation)
            throw new AccessDeniedException("management_unavailable");
        var current = reader.Current with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, reader.Agent.Instance) };
        if (!TenantManagementReadContract.Allows(reader.Run) || !TenantManagementReadContract.Allows(current, tool)
            || !TenantManagementReadContract.Allows(reader.Run.Admission.Configuration!.Definition, tool)) throw new AccessDeniedException("management_read_unavailable");
    }
    public static string ConfigurationHash(AgentInstance instance) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(instance));
    // Only the three already implemented direct/approved preference fields may be
    // explained by this task's completed receipt chain. Every other field stays exact.
    public static string StableConfigurationHash(AgentInstance instance)
    {
        var body = JsonSerializer.SerializeToNode(instance)!.AsObject(); body.Remove("Revision"); body.Remove("UpdatedAt");
        var settings = body["SettingsOverrides"]?.AsObject() ?? new JsonObject();
        foreach (var field in new[] { "ResponseLength", "Language", "AskOneQuestionAtATime" }) settings.Remove(field);
        foreach (var key in settings.Where(p => p.Value is null).Select(p => p.Key).ToArray()) settings.Remove(key);
        body["SettingsOverrides"] = settings; return TenantMemoryOperationRules.Hash(body.ToJsonString());
    }
    public static PreparedManagementRead Build(TenantManagementReader reader, AgentDefinition baseline, TenantManagementReadArguments args, object? inspection = null, string? sources = null)
    {
        var owner = reader.Agent.Instance; object data;
        var excerpts = new List<string>();
        if (TenantManagementInspectionSources.IsInspection(args.Tool)) data = inspection ?? throw new AccessDeniedException("management_inspection_unavailable");
        else if (args.Tool == "management.inspect") data = HarnessManagementRules.View(owner, baseline);
        else if (args.Tool == "automation.preferences.inspect") data = new { owner.UnattendedModelCatalogKey, owner.UnattendedReasoningEffort };
        else if (args.Tool == "settings.list") data = InstanceSettingsResolver.Sections.Select(section => new { section,
            hasOverrides = InstanceSettingsResolver.Overrides(owner.SettingsOverrides ?? new(), section).Count > 0 });
        else
        {
            var view = InstanceSettingsRules.View(owner, baseline, args.Section!);
            // Host initialization, limits and setup never become model configuration facts.
            var body = JsonSerializer.SerializeToNode(view, Json)!.AsObject();
            foreach (var part in new[] { "overrides", "effective", "sources", "definitionDefaults", "constraints" })
            {
                var values = body[part]!.AsObject();
                foreach (var key in values.Select(p => p.Key).ToArray())
                {
                    if (view.Section == "sandboxPolicy" && key != "profileId") { values.Remove(key); continue; }
                    if (values[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 512)
                    { values[key] = TenantContinuityRules.Safe(text, 512, out _); excerpts.Add(part + "." + key); }
                }
            }
            data = body;
        }
        var bounded = JsonSerializer.SerializeToNode(data, Json);
        void Bound(JsonNode? node, string path)
        {
            // A displayed remedy is an exact replacement set, never an excerpt.
            if (TenantManagementInspectionSources.IsInspection(args.Tool) && path.StartsWith("data.remediationOptions", StringComparison.Ordinal)) return;
            if (node is JsonObject map)
                foreach (var key in map.Select(p => p.Key).ToArray())
                {
                    if ((TenantManagementReadContract.IsChildTool(args.Tool) || args.Tool == ToolCatalog.SkillsLoad) && key is "procedure" or "content") continue;
                    if (map[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 512)
                    { map[key] = TenantContinuityRules.Safe(text, 512, out _); excerpts.Add(path + "." + key); }
                    else Bound(map[key], path + "." + key);
                }
            else if (node is JsonArray array)
            {
                var limit = args.Tool == "settings.list" ? 32 : 12;
                if (array.Count > limit) { excerpts.Add(path + " (" + array.Count + " items)"); while (array.Count > limit) array.RemoveAt(array.Count - 1); }
                for (var index = 0; index < array.Count; index++) Bound(array[index], path + "[" + index + "]");
            }
        }
        Bound(bounded, "data"); data = bounded!;
        var observation = JsonSerializer.Serialize(new { tool = args.Tool, instanceId = owner.InstanceId, instanceRevision = owner.Revision,
            definitionId = owner.DefinitionId, definitionVersion = owner.ActiveVersion, currentRunVersion = reader.Run.DefinitionVersion,
            changesApplyTo = args.Tool == ToolCatalog.SkillsLoad ? "currentRunRead" : "nextRun", observedSnapshot = true, data, excerptFields = excerpts }, Json);
        if (Encoding.UTF8.GetByteCount(observation) > 16000) throw new AccessDeniedException("management_read_budget");
        var hash = ConfigurationHash(owner); var stable = StableConfigurationHash(owner);
        return new(reader.Run.SessionId, reader.Root.SessionId, args.Tool, args.Json, owner.Revision, hash, stable, observation,
            Hash(args.Tool, args.Json, owner.Revision, hash, stable, observation, sources), sources);
    }
    public static string Hash(string tool, string args, long revision, string configuration, string stable, string observation, string? sources = null) =>
        TenantMemoryOperationRules.Hash(sources is null ? JsonSerializer.Serialize(new { tool, args, revision, configuration, stable, observation })
            : JsonSerializer.Serialize(new { tool, args, revision, configuration, stable, observation, sources }));
    public static void Validate(PreparedManagementRead value)
    {
        var args = TenantManagementReadContract.Parse(value.ToolName, value.ArgumentsJson);
        if (value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty || args.Json != value.ArgumentsJson || value.InstanceRevision < 1
            || !TenantMemoryOperationRules.ValidHash(value.ConfigurationHash) || !TenantMemoryOperationRules.ValidHash(value.StableConfigurationHash)
            || Encoding.UTF8.GetByteCount(value.ObservationJson) > 16000 || value.ObservationHash != Hash(value.ToolName, value.ArgumentsJson, value.InstanceRevision, value.ConfigurationHash, value.StableConfigurationHash, value.ObservationJson, value.SourcesJson))
            throw new ArgumentException("Use an exact bounded management observation.");
        if (TenantManagementInspectionSources.IsInspection(value.ToolName) != (value.SourcesJson is not null)) throw new ArgumentException("Use exact compound management sources.");
        var sources = TenantManagementInspectionSources.Read(value.SourcesJson);
        if (TenantManagementReadContract.IsChildTool(value.ToolName))
        {
            var expectedKind = value.ToolName == "skills.inspect" ? "skillProcedure" : value.ToolName == "skills.list" ? "skill" : "resource";
            if (sources.Count > 12 || sources.Any(s => s.Kind != expectedKind)) throw new ArgumentException("Use exact child read witnesses.");
            if (value.ToolName.EndsWith(".inspect", StringComparison.Ordinal))
            {
                using var selection = JsonDocument.Parse(args.Json);
                if (sources.Count != 1 || sources[0].Key != selection.RootElement.GetProperty("key").GetString()) throw new ArgumentException("Use one exact selected child witness.");
            }
        }
        if (value.ToolName == ToolCatalog.SkillsLoad)
        {
            var ids = TenantSkillLoadContract.Parse(value.ArgumentsJson);
            if (sources.Count != ids.Count || sources.Any(s => s.Kind != "skillProcedure" || !ids.Contains(s.Key)))
                throw new ArgumentException("Use exact pinned procedure witnesses.");
        }
        using var doc = JsonDocument.Parse(value.ObservationJson); if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an observation object.");
    }
    // The owning store has resolved these exact scopes in its current snapshot.
    // Do not open a second ownership transaction from inside the effect transaction.
    public static async ValueTask<bool> NeedsReviewOwnedAsync(TenantManagementReader reader, string permission, AccessScope scope, IamAuthorizer iam, CancellationToken ct)
    {
        var policy = TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get(permission));
        return policy.Mode == OperationApprovalMode.Always || policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission
            && reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human
            && !(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed;
    }
    public static async ValueTask RawAsync(TenantManagementReader reader, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        if (!(await iam.CheckOwnedTenantAsync(reader.Security, human ?? reader.Agent.PrincipalId, PermissionCatalog.Get("agents.read"), Scope(reader), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException(human is null ? "management_read_required" : "approval_unavailable");
    }
    public static void ValidateCurrent(TenantManagementReader reader, AgentDefinition baseline, PreparedManagementRead value,
        IEnumerable<TenantOperationReceipt> receipts, Func<Guid, TenantApprovalRequest?> request)
    {
        Validate(value); TenantSessionRules.RequireExecutor(reader.Security, reader.Agent);
        if (value.ToolName == ToolCatalog.SkillsLoad) TenantSkillLoadRules.Eligible(reader);
        else
        {
            if (reader.Root.Admission.ExecutionContext?.Origin != ExecutionOrigin.UserMessage || reader.Root.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
                || !TenantManagementReadContract.Allows(reader.Run) || !TenantManagementReadContract.Allows(reader.Run.Admission.Configuration!.Definition, value.ToolName))
                throw new AccessDeniedException("management_read_unavailable");
        }
        if (value.SessionId != reader.Run.SessionId || value.RootSessionId != reader.Root.SessionId) throw new AccessDeniedException("management_source_changed");
        var owner = reader.Agent.Instance;
        if (owner.Revision == value.InstanceRevision && ConfigurationHash(owner) == value.ConfigurationHash) { Eligible(reader, baseline, value.ToolName); return; }
        if (owner.Revision <= value.InstanceRevision || owner.Revision - value.InstanceRevision > 1024) throw new AccessDeniedException("management_source_changed");
        var rows = receipts.Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.InstanceId == owner.InstanceId && r.ExecutorPrincipalId == reader.Agent.PrincipalId
            && r.SessionId == reader.Run.SessionId && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed
            && r.InstanceRevision > value.InstanceRevision && r.InstanceRevision <= owner.Revision).ToArray();
        if (rows.Length > 1024) throw new AccessDeniedException("management_source_changed");
        var stable = value.StableConfigurationHash;
        for (var revision = value.InstanceRevision + 1; revision <= owner.Revision; revision++)
        {
            var candidates = rows.Where(r => r.InstanceRevision == revision && (Preference(r, request, reader.Run) || Management(r, request, stable) is not null || Child(r, request, stable) is not null)).ToArray();
            if (candidates.Length != 1) throw new AccessDeniedException("management_source_changed");
            if (Child(candidates[0], request, stable) is { } child) stable = child.AfterHash;
            else if (Management(candidates[0], request, stable) is { } mutation) stable = mutation.AfterStableHash;
        }
        if (StableConfigurationHash(owner) != stable) throw new AccessDeniedException("management_source_changed");
        // A completed exact self-change may remove its own tool/delegation. It does
        // not erase the task's already observed historical snapshot or add new read authority.
        // External edits still need an unbroken receipt for every Instance revision.
    }
    private static TenantManagementSkillReceipt? Skill(TenantOperationReceipt receipt, Func<Guid, TenantApprovalRequest?> request, string before)
    {
        if (receipt.ManagementSkill is not { } value || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantManagementMutationRules.Completed
            || !TenantManagementSkillRules.IsTool(value.ToolName) || value.BeforeStableHash != before || value.BeforeInstanceRevision != receipt.InstanceRevision - 1
            || !TenantMemoryOperationRules.ValidHash(value.AfterStableHash) || value.BeforeSources is null || value.AfterSources is null) return null;
        var beforeSources = TenantManagementInspectionSources.Read(JsonSerializer.Serialize(value.BeforeSources));
        var afterSources = TenantManagementInspectionSources.Read(JsonSerializer.Serialize(value.AfterSources));
        if (beforeSources.Count > 4 || afterSources.Count > 4 || beforeSources.Concat(afterSources).Any(s => s.Kind is not ("skill" or "skillProcedure")
            || !s.ResourceId.StartsWith(receipt.InstanceId.ToString("D") + ":", StringComparison.Ordinal))) return null;
        if (receipt.RequestId is { } id)
        {
            if (request(id) is not { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation: { Skill: not null } mutation } approved
                || approved.Operation.ActionHash != receipt.ActionHash || approved.Operation.ExpectedInstanceRevision != value.BeforeInstanceRevision
                || JsonSerializer.Serialize(TenantManagementSkillRules.Evidence(mutation, value.BeforeInstanceRevision)) != JsonSerializer.Serialize(value)) return null;
        }
        else if (receipt.SourceCallId is not { Length: > 0 } || receipt.ParametersHash is not { } parameters || !TenantMemoryOperationRules.ValidHash(parameters)) return null;
        return value;
    }
    private static (IReadOnlyList<TenantManagementChildRead> Before, IReadOnlyList<TenantManagementChildRead> After, string AfterHash)? Child(
        TenantOperationReceipt receipt, Func<Guid, TenantApprovalRequest?> request, string before)
    {
        if (receipt.RequestId is { } bindingRequestId && request(bindingRequestId) is { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation.Binding: { } binding } bindingRequest
            && receipt.Outcome == TenantOperationOutcome.Completed && receipt.ReasonCode == TenantManagementMutationRules.Completed
            && bindingRequest.Operation.ActionHash == receipt.ActionHash && bindingRequest.Operation.ExpectedInstanceRevision == receipt.InstanceRevision - 1
            && bindingRequest.Operation.ManagementMutation.BeforeStableHash == before)
            return (binding.BeforeSources, binding.AfterSources, bindingRequest.Operation.ManagementMutation.AfterStableHash);
        if (Skill(receipt, request, before) is { } skill) return (skill.BeforeSources, skill.AfterSources, skill.AfterStableHash);
        if (receipt.ManagementResource is not { } resource || resource.BeforeStableHash != before
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantManagementMutationRules.Completed
            || resource.BeforeInstanceRevision != receipt.InstanceRevision - 1) return null;
        if (receipt.RequestId is { } id)
        {
            if (request(id) is not { } approved || !TenantManagementResourceRules.CompletedChange(approved, receipt)) return null;
        }
        else if (receipt.SourceCallId is not { Length: > 0 } || receipt.ParametersHash is not { } parameters || !TenantMemoryOperationRules.ValidHash(parameters)
            || resource.HarnessChangeId is null && (resource.ToolName != "resources.upsert" || resource.BeforeSources.Count != 0 || resource.AfterSources.Count != 1
                || resource.Result.Revision != 1 || !resource.Result.Key.StartsWith("instance:", StringComparison.Ordinal))) return null;
        _ = TenantManagementResourceRules.ReadEvidence(JsonSerializer.Serialize(resource));
        if (resource.BeforeSources.Concat(resource.AfterSources).Any(s => s.Kind != "resource"
            || s.ResourceId != receipt.InstanceId.ToString("D") + ":" + s.Key)) return null;
        return (resource.BeforeSources, resource.AfterSources, resource.AfterStableHash);
    }
    public static TenantManagementSourceRetention RetainedSources(TenantManagementReader reader, PreparedManagementRead value,
        IEnumerable<TenantOperationReceipt> receipts, Func<Guid, TenantApprovalRequest?> request)
    {
        var sources = TenantManagementInspectionSources.Read(value.SourcesJson).ToList(); var deleted = new HashSet<string>(StringComparer.Ordinal);
        var stable = value.StableConfigurationHash;
        foreach (var revision in Enumerable.Range(1, checked((int)(reader.Agent.Instance.Revision - value.InstanceRevision))).Select(offset => value.InstanceRevision + offset))
        {
            var rows = receipts.Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.InstanceId == reader.Agent.Instance.InstanceId && r.ExecutorPrincipalId == reader.Agent.PrincipalId
                && r.SessionId == reader.Run.SessionId && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.InstanceRevision == revision).ToArray();
            var row = rows.Single(r => Preference(r, request, reader.Run) || Management(r, request, stable) is not null || Child(r, request, stable) is not null);
            if (Child(row, request, stable) is { } child)
            {
                foreach (var old in child.Before)
                {
                    var index = sources.FindIndex(s => s.Kind == old.Kind && s.Key == old.Key);
                    if (index < 0) continue;
                    if (deleted.Contains(old.Kind + ":" + old.Key)) continue;
                    if (sources[index] != old) throw new AccessDeniedException("management_source_changed");
                    var next = child.After.SingleOrDefault(s => s.Kind == old.Kind && s.Key == old.Key);
                    if (next is null) deleted.Add(old.Kind + ":" + old.Key); else sources[index] = next;
                }
                stable = child.AfterHash;
            }
            else if (Management(row, request, stable) is { } mutation) stable = mutation.AfterStableHash;
        }
        return new(value.SourcesJson is null ? null : JsonSerializer.Serialize(sources), deleted);
    }
    public static TenantResourcePinRetention? RetainedResourcePin(TenantManagementReader reader, EffectiveAgentResource resource,
        TenantAgentConfigurationSnapshot snapshot, IReadOnlyList<AgentDefinitionPublicationResource> published,
        IEnumerable<TenantOperationReceipt> receipts, Func<Guid, TenantApprovalRequest?> request)
    {
        var pin = reader.Run.Admission.Configuration ?? throw new AccessDeniedException("resource_source_changed");
        var owner = reader.Agent.Instance;
        if (!pin.Resources.Contains(resource) || owner.Revision <= pin.InstanceRevision) return null;
        var rows = receipts.Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.InstanceId == owner.InstanceId && r.ExecutorPrincipalId == reader.Agent.PrincipalId
            && r.SessionId == reader.Run.SessionId && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed
            && r.InstanceRevision > pin.InstanceRevision && r.InstanceRevision <= owner.Revision).ToArray();
        if (!rows.Any(r => r.ManagementResource is { } change && change.BeforeSources.Any(s => s.Key == resource.Key
            && change.AfterSources.SingleOrDefault(next => next.Kind == s.Kind && next.Key == s.Key) != s))) return null;
        if (owner.Revision - pin.InstanceRevision > 1024 || owner.DefinitionId != reader.Run.DefinitionId || owner.ActiveVersion != reader.Run.DefinitionVersion)
            throw new AccessDeniedException("resource_source_changed");
        var stable = rows.OrderBy(r => r.InstanceRevision).Select(r => r.ManagementResource?.BeforeStableHash ?? r.ManagementSkill?.BeforeStableHash
            ?? (r.RequestId is { } id ? request(id)?.Operation.ManagementMutation?.BeforeStableHash : null)).FirstOrDefault(s => s is not null)
            ?? throw new AccessDeniedException("resource_source_changed");
        TenantManagementChildRead? witness = null; long? originalRevision = null; var deleted = false;
        for (var revision = pin.InstanceRevision + 1; revision <= owner.Revision; revision++)
        {
            var candidates = rows.Where(r => r.InstanceRevision == revision && (Preference(r, request, reader.Run) || Child(r, request, stable) is not null || Management(r, request, stable) is not null)).ToArray();
            if (candidates.Length != 1) throw new AccessDeniedException("resource_source_changed");
            var receipt = candidates[0];
            if (Child(receipt, request, stable) is { } child)
            {
                var beforeResource = receipt.RequestId is { } id ? request(id)?.Operation.ManagementMutation?.Resource?.Before : receipt.ManagementResource?.Before;
                if (receipt.ManagementResource is { } resourceChange && (beforeResource?.Key == resource.Key || resourceChange.Result.Key == resource.Key))
                {
                    if (deleted || beforeResource is not { } before) throw new AccessDeniedException("resource_source_changed");
                    var old = child.Before.SingleOrDefault(s => s.Kind == "resource" && s.Key == resource.Key) ?? throw new AccessDeniedException("resource_source_changed");
                    if (originalRevision is null)
                    {
                        if (!before.Enabled || before.LogicalPath != resource.LogicalPath || before.Kind != resource.Kind || before.MediaType != resource.MediaType
                            || before.ContentSha256 != resource.ContentSha256 || before.ByteLength != resource.ByteLength) throw new AccessDeniedException("resource_source_changed");
                        originalRevision = before.Revision;
                    }
                    if (witness is not null && witness != old) throw new AccessDeniedException("resource_source_changed");
                    witness = child.After.SingleOrDefault(s => s.Kind == "resource" && s.Key == resource.Key); deleted = witness is null;
                }
                stable = child.AfterHash;
            }
            else if (Management(receipt, request, stable) is { } mutation) stable = mutation.AfterStableHash;
        }
        if (originalRevision is null || stable != StableConfigurationHash(owner)) throw new AccessDeniedException("resource_source_changed");
        var current = TenantManagementInspectionSources.ResourceItems(reader, published, snapshot.Resources, snapshot.Skills).SingleOrDefault(s => s.Key == resource.Key);
        if (deleted ? current is not null : current is null || witness is null || current.Fingerprint != witness.Fingerprint || current.Scope.ResourceId != witness.ResourceId)
            throw new AccessDeniedException("resource_source_changed");
        return new(originalRevision.Value, deleted, owner.Revision);
    }
    private static PreparedManagementMutation? Management(TenantOperationReceipt receipt, Func<Guid, TenantApprovalRequest?> request, string before)
    {
        if (receipt.RequestId is not { } id || request(id) is not { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation: { } value } approved
            || value.Skill is not null || value.Resource is not null
            || receipt.ReasonCode != TenantManagementMutationRules.Completed || approved.Operation.ActionHash != receipt.ActionHash
            || approved.Operation.ExpectedInstanceRevision != receipt.InstanceRevision - 1 || value.BeforeStableHash != before) return null;
        TenantManagementMutationRules.Validate(value); return value;
    }

    private static bool Preference(TenantOperationReceipt receipt, Func<Guid, TenantApprovalRequest?> request, AgentRun source)
    {
        if (receipt.RequestId is { } id)
            return request(id) is { Status: TenantApprovalStatus.Approved, Operation.Preferences: not null } approved
                && receipt.ReasonCode == "operation_completed" && approved.Operation.ActionHash == receipt.ActionHash
                && approved.Operation.ExpectedInstanceRevision == receipt.InstanceRevision - 1;
        return receipt.ReasonCode == "operation_completed" && receipt.SourceCallId is { Length: > 0 } && receipt.ParametersHash is { } parameters && TenantMemoryOperationRules.ValidHash(parameters)
            && receipt.ActionHash == TenantPreferenceToolContract.ActionHash(source, receipt.InstanceRevision - 1, parameters);
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.ManagementRead is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Completed
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
}
