using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantManagementSkillRules
{
    public static TenantManagementSkillReceipt ReadEvidence(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 12000) throw new ArgumentException("Use bounded Skill effect evidence.");
        var value = JsonSerializer.Deserialize<TenantManagementSkillReceipt>(json) ?? throw new ArgumentException("Use Skill effect evidence.");
        if (JsonSerializer.Serialize(value) != json || !IsTool(value.ToolName) || value.BeforeInstanceRevision < 1
            || !TenantMemoryOperationRules.ValidHash(value.BeforeStableHash) || !TenantMemoryOperationRules.ValidHash(value.AfterStableHash)
            || value.BeforeSources is null || value.AfterSources is null || value.BeforeSources.Count > 4 || value.AfterSources.Count > 4
            || value.Result is null || !SkillKeys.IsValid(value.Result.Key) || value.Result.Revision < 1 || value.Result.Deleted != (value.ToolName == "skills.delete")
            || value.ToolName == "skills.customize" && (value.Result.SourceKey is not { } source || !source.StartsWith("definition:", StringComparison.Ordinal) || !SkillKeys.IsValid(source)
                || value.Result.SourceRevision is not >= 1 || value.Result.SourceEnabled != false)
            || value.ToolName != "skills.customize" && (value.Result.SourceKey is not null || value.Result.SourceRevision is not null || value.Result.SourceEnabled is not null))
            throw new ArgumentException("Use exact Skill effect evidence.");
        foreach (var sources in new[] { value.BeforeSources, value.AfterSources })
            if (TenantManagementInspectionSources.Read(JsonSerializer.Serialize(sources)).Any(s => s.Kind is not ("skill" or "skillProcedure")))
                throw new ArgumentException("Use Skill source witnesses.");
        return value;
    }
    public static TenantManagementSkillReceipt Evidence(PreparedManagementMutation value, long beforeRevision)
    {
        var skill = value.Skill ?? throw new ArgumentException("Use a Skill effect.");
        return new(value.ToolName, beforeRevision, value.BeforeStableHash, value.AfterStableHash, skill.BeforeSources, skill.AfterSources, skill.Result);
    }
    public static object? SkillMetadata(TenantManagementSkillResult result, bool instanceOnly = false) => result.Deleted || instanceOnly && !result.Key.StartsWith("instance:", StringComparison.Ordinal)
        ? null : new { key = result.Key, revision = result.Revision, enabled = result.Enabled };
    public static object? DefinitionMetadata(TenantManagementSkillResult result) => result.SourceKey is null ? null
        : new { key = result.SourceKey, revision = result.SourceRevision, enabled = result.SourceEnabled };
    public static bool Visible(TenantApprovalRequest request, TenantManagementApprovalVisibility? visibility, long? currentInstanceRevision, TenantOperationReceipt? recorded)
    {
        if (request.Operation.ManagementMutation?.Skill is not { } plan) return true;
        bool All(TenantManagementChildVisibility? permission) => permission is { Agents: null };
        var all = All(visibility?.Skills) && All(visibility?.SkillChanges);
        if (plan.CreateScope) return all;
        var resource = request.Operation.InstanceId.ToString("D") + ":" + plan.TargetKey;
        bool Has(TenantManagementChildVisibility? permission) => permission is not null
            && (permission.Agents is null || permission.Agents.Contains(request.Operation.InstanceId) || permission.Children?.Contains(resource) == true);
        return Has(visibility?.Skills) && Has(visibility?.SkillChanges)
            && (all || currentInstanceRevision == request.Operation.ExpectedInstanceRevision
                || CompletedChange(request, recorded) && currentInstanceRevision == recorded!.InstanceRevision);
    }
    public static bool CompletedChange(TenantApprovalRequest request, TenantOperationReceipt? receipt) => request is { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation.Skill: not null }
        && receipt is { Outcome: TenantOperationOutcome.Completed, ManagementSkill: not null } && receipt.ReasonCode == TenantManagementMutationRules.Completed
        && receipt.RequestId == request.RequestId && receipt.OperationId == request.Operation.OperationId && receipt.TenantId == request.TenantId
        && receipt.ExecutorPrincipalId == request.ExecutorPrincipalId && receipt.InstanceId == request.Operation.InstanceId && receipt.SessionId == request.SessionId
        && receipt.RootActivationId == request.RootActivationId && receipt.ActionHash == request.Operation.ActionHash
        && JsonSerializer.Serialize(receipt.ManagementSkill) == JsonSerializer.Serialize(Evidence(request.Operation.ManagementMutation!, request.Operation.ExpectedInstanceRevision));
    public static bool CompletedDeletion(TenantApprovalRequest request, TenantOperationReceipt? receipt) => request.Operation.ManagementMutation?.Skill?.Result.Deleted == true && CompletedChange(request, receipt);
    public static bool IsTool(string tool) => tool is "skills.create" or "skills.update" or "skills.set_enabled" or "skills.reset_enabled" or "skills.delete" or "skills.customize";
    public static AccessScope Scope(TenantManagementReader reader, TenantManagementMutationArguments args) => args.Tool is "skills.create" or "skills.customize"
        ? AccessScope.ForTenant(reader.Security.Tenant.TenantId)
        : TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "skill", args.Value.GetProperty("key").GetString()!);
    public static AccessScope Scope(TenantManagementReader reader, PreparedManagementMutation value) => Scope(reader, TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson));
    public static bool AutoApplies(TenantManagementReader reader, string tool) => tool is not ("skills.delete" or "skills.customize")
        && reader.Run.Admission.Configuration!.Definition.SelfManagement?.AutoApplies(ManagementAutoApply.SkillChanges) == true
        && ManagementPolicyResolver.Resolve(reader.Baseline, reader.Agent.Instance).AutoApplies(ManagementAutoApply.SkillChanges);

    internal static TenantPreparedSkillChange Prepare(TenantManagementSource source, TenantManagementMutationArguments args, DateTimeOffset at)
    {
        var value = args.Value;
        return TenantAgentSkillPreparation.Prepare(source.Reader.Agent.Instance, source.Reader.Baseline, source.Snapshot.Skills,
            args.Tool[7..], value.TryGetProperty("key", out var key) ? key.GetString() : null,
            value.TryGetProperty("expectedRevision", out var revision) ? revision.GetInt64() : null,
            args.Tool is "skills.create" or "skills.update" ? InstanceSkillTools.ReadInput(value) : null,
            value.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : null, SkillAuthor.Agent, at);
    }
    public static async ValueTask RawAsync(TenantManagementReader reader, TenantManagementMutationArguments args, IamAuthorizer iam, Guid actor, CancellationToken ct)
    {
        await TenantManagementReadRules.RawAsync(reader, iam, ct, actor == reader.Agent.PrincipalId ? null : actor).ConfigureAwait(false);
        var scopes = new List<AccessScope> { Scope(reader, args) };
        if (args.Tool == "skills.customize") scopes.Add(TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "skill", args.Value.GetProperty("key").GetString()!));
        foreach (var scope in scopes)
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get("skills.read"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("skill_read_required");
    }
    public static PreparedManagementMutation Build(TenantManagementSource source, TenantManagementMutationArguments args, DateTimeOffset at)
    {
        var reader = source.Reader; var owner = reader.Agent.Instance;
        TenantManagementMutationRules.Eligible(reader, args.Tool);
        if (args.ExpectedInstanceRevision != owner.Revision) throw new SecurityConflictException("stale_revision");
        var prepared = Prepare(source, args, at); var mutation = prepared.Change?.Skill;
        var after = prepared.Change is null ? owner : TenantAgentConfigurationRules.Apply(owner, prepared.Change, at);
        var affected = new[] { args.Value.TryGetProperty("key", out var target) ? target.GetString() : null, prepared.Skill.Key }.Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        TenantManagementChildRead[] Witnesses(TenantManagementReader sourceReader, InstanceSkillSnapshot skills) => TenantManagementInspectionSources.SkillItems(sourceReader, skills)
            .Where(i => affected.Contains(i.Key)).OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal)
            .Select(i => new TenantManagementChildRead(i.Kind, i.Key, i.Fingerprint, i.Scope.ResourceId!)).ToArray();
        var plan = new PreparedManagementSkill(reader.Security.Tenant.TenantId,
            args.Value.TryGetProperty("key", out var key) ? key.GetString() : null, args.Tool is "skills.create" or "skills.customize",
            TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(source.Snapshot.Skills)), at, mutation?.DefinitionState, mutation?.InstanceSkill,
            mutation?.DeleteSkillId, mutation?.ExpectedSkillRevision, mutation?.ExpectedStateRevision, prepared.Change is not null,
            Witnesses(reader, source.Snapshot.Skills), Witnesses(reader with { Agent = reader.Agent with { Instance = after } }, prepared.Next),
            new(prepared.Skill.Key, prepared.Skill.Revision, prepared.Skill.Enabled, args.Tool == "skills.delete",
                prepared.DefinitionSkill?.Key, prepared.DefinitionSkill?.Revision, prepared.DefinitionSkill?.Enabled));
        var definition = InstanceSettingsResolver.Resolve(reader.Baseline, owner.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(reader.Baseline, owner) };
        var before = plan.TargetKey is null ? null : AgentInstanceSkillService.Views(definition, source.Snapshot.Skills)
            .Concat(AgentInstanceSkillService.BuiltIns(definition, definition.SelfManagement!, owner)).Single(s => s.Key == plan.TargetKey);
        var rows = new[] { new TenantApprovalFieldChange("Skill", before, args.Tool == "skills.delete" ? null : prepared.Skill),
            new TenantApprovalFieldChange("Inherited source choice", null, prepared.DefinitionSkill),
            new TenantApprovalFieldChange("Effect boundary", "Current Run remains pinned", "Only this Instance changes for future Runs. Skill requirements grant no capabilities or resource reads.") };
        var changes = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", Encoding.UTF8.GetBytes(changes));
        var result = new PreparedManagementMutation(reader.Run.SessionId, reader.Root.SessionId, args.Tool, args.Json,
            TenantManagementReadRules.ConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(after),
            changes, Management: prepared.Change?.HarnessManagement, Skill: plan);
        TenantManagementMutationRules.Validate(result); return result;
    }
    public static TenantAgentConfigurationChange? Change(TenantManagementSource source, PreparedManagementMutation value)
    {
        var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson); var plan = value.Skill ?? throw new AccessDeniedException("management_source_changed");
        var rebuilt = Build(source, args, plan.PreparedAt);
        if (!TenantManagementMutationRules.Matches(value, rebuilt)) throw new AccessDeniedException("management_source_changed");
        return Prepare(source, args, plan.PreparedAt).Change;
    }
}
