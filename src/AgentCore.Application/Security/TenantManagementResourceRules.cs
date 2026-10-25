using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantManagementResourceRules
{
    public static bool AutoApplies(TenantManagementReader reader, PreparedManagementMutation value) => value.Harness is null && value.ToolName == "resources.upsert" && value.Resource is { TargetKey: null, CreateScope: true }
        && reader.Run.Admission.Configuration!.Definition.SelfManagement?.AutoApplies(ManagementAutoApply.TextResources) == true
        && ManagementPolicyResolver.Resolve(reader.Baseline, reader.Agent.Instance).AutoApplies(ManagementAutoApply.TextResources);
    public static bool IsTool(string tool) => tool is "resources.upsert" or "resources.copy" or "resources.set_enabled" or "resources.delete";
    public static string Permission(string tool) => tool == "resources.delete" ? "resources.delete" : "resources.manage";
    public static bool Creates(TenantManagementMutationArguments args) => args.Tool == "resources.copy" || args.Tool == "resources.upsert" && !args.Value.TryGetProperty("key", out _);
    public static AccessScope Scope(TenantManagementReader reader, TenantManagementMutationArguments args) => Creates(args)
        ? AccessScope.ForTenant(reader.Security.Tenant.TenantId)
        : TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "resource", args.Value.GetProperty("key").GetString()!);
    public static AccessScope Scope(TenantManagementReader reader, PreparedManagementMutation value) => Scope(reader, TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson));

    public static void Validate(PreparedManagementMutation value, TenantManagementMutationArguments args)
    {
        var plan = value.Resource ?? throw new ArgumentException("Use a typed Resource plan.");
        bool Key(string key) { try { _ = InstanceResourceRules.ParseKey(key); return true; } catch (Sessions.AgentCoreException) { return false; } }
        if (!IsTool(args.Tool) || plan.TenantId == Guid.Empty || plan.AllocatedId == Guid.Empty || !TenantMemoryOperationRules.ValidHash(plan.SnapshotHash)
            || plan.Result is null || !Key(plan.Result.Key) || plan.Result.Revision < (plan.Result.Key.StartsWith("definition:", StringComparison.Ordinal) ? 0 : 1)
            || plan.Result.Deleted != (args.Tool == "resources.delete") || plan.CreateScope != Creates(args)
            || plan.TargetKey != (args.Value.TryGetProperty("key", out var selected) ? selected.GetString() : null)
            || value.Settings is not null || value.Management is not null || value.AutomationChanges is not null || value.Skill is not null
            || value.UnattendedOfferingId is not null || value.UnattendedReasoningEffort is not null
            || value.Harness is { } harness && harness.Changed != plan.Changed
            || (args.Tool == "resources.copy") != (plan.CopiedSource is not null) || (args.Tool == "resources.upsert") != (plan.Source is not null)
            || plan.Changed != (new[] { plan.DefinitionState is not null, plan.InstanceResource is not null, plan.DeleteResourceId is not null }.Count(b => b) == 1)
            || !plan.Changed && (plan.DefinitionState is not null || plan.InstanceResource is not null || plan.DeleteResourceId is not null
                || plan.ExpectedResourceRevision is not null || plan.ExpectedStateRevision is not null || value.BeforeStableHash != value.AfterStableHash)
            || plan.BeforeSources is null || plan.AfterSources is null || plan.BeforeSources.Count > 2 || plan.AfterSources.Count > 2
            || plan.Source is { } material && (material.Name != args.Value.GetProperty("source").GetString() || material.RootRunId == Guid.Empty
                || !TenantMemoryOperationRules.ValidHash(material.MaterialHash) || material.ReadRequestId.HasValue != material.ReadOperationId.HasValue
                || material.Name == "conversation:user" && (material.ToolCallId is not null || material.ToolName is not null || material.ArgumentsJson is not null
                    || material.ReadRequestId is not null || material.Workspace is not null || material.Attachment is not null)
                || material.Name != "conversation:user" && (material.ToolCallId is not { Length: > 0 and <= 200 } || material.ToolName is not { Length: > 0 and <= 100 }
                    || material.ArgumentsJson is not { Length: > 0 and <= 262144 }
                    || new[] { material.Workspace is not null, material.Attachment is not null }.Count(b => b) > 1)))
            throw new ArgumentException("Use one exact bounded Resource plan.");
        ValidateResult(plan.Result);
        if ((plan.TargetKey is null) != (plan.Before is null) || plan.Before is { } before && (before.Key != plan.TargetKey || before.Deleted))
            throw new ArgumentException("Use the exact observed Resource before-state.");
        if (plan.Before is { } previous)
        {
            ValidateResult(previous);
            if (plan.BeforeSources.SingleOrDefault(s => s.Key == previous.Key)?.Fingerprint != Fingerprint(previous)) throw new ArgumentException("Use the exact Resource before witness.");
        }
        foreach (var sources in new[] { plan.BeforeSources, plan.AfterSources })
            if (TenantManagementInspectionSources.Read(JsonSerializer.Serialize(sources)).Any(s => s.Kind != "resource" || !Key(s.Key)))
                throw new ArgumentException("Use Resource source witnesses.");
    }
    private static void ValidateResult(TenantManagementResourceResult result)
    {
        if (!Enum.IsDefined(result.Kind) || result.Kind == AgentDefinitionResourceKind.EvalFixture || !TenantMemoryOperationRules.ValidHash(result.ContentSha256)
            || result.LogicalPath != DefinitionResourcePolicies.NormalizeLogicalPath(result.LogicalPath)
            || result.MediaType != DefinitionResourcePolicies.NormalizeMediaType(result.MediaType)) throw new ArgumentException("Use exact Resource metadata.");
        DefinitionResourcePolicies.ValidateContentSize(result.ByteLength);
    }
    public static string Fingerprint(TenantManagementResourceResult resource) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new {
        resource.Key, Origin = resource.Key.StartsWith("definition:", StringComparison.Ordinal) ? "Definition" : "Instance",
        resource.LogicalPath, resource.Kind, resource.MediaType, resource.ContentSha256, resource.ByteLength, resource.EnabledOverride, resource.Revision }));
    public static TenantManagementResourceReceipt Evidence(PreparedManagementMutation value, long beforeRevision)
    {
        var plan = value.Resource ?? throw new ArgumentException("Use a Resource effect.");
        return new(value.ToolName, beforeRevision, value.BeforeStableHash, value.AfterStableHash, plan.BeforeSources, plan.AfterSources, plan.Result, value.Harness?.Assessment.OperationId, value.Harness is null ? null : plan.Before);
    }
    public static TenantManagementResourceReceipt ReadEvidence(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 12000) throw new ArgumentException("Use bounded Resource effect evidence.");
        var value = JsonSerializer.Deserialize<TenantManagementResourceReceipt>(json) ?? throw new ArgumentException("Use Resource effect evidence.");
        if (JsonSerializer.Serialize(value) != json || !IsTool(value.ToolName) || value.BeforeInstanceRevision < 1
            || !TenantMemoryOperationRules.ValidHash(value.BeforeStableHash) || !TenantMemoryOperationRules.ValidHash(value.AfterStableHash)
            || value.BeforeSources is null || value.AfterSources is null || value.BeforeSources.Count > 2 || value.AfterSources.Count > 2
            || value.Result is null || value.Result.Deleted != (value.ToolName == "resources.delete")) throw new ArgumentException("Use exact Resource effect evidence.");
        var parsed = InstanceResourceRules.ParseKey(value.Result.Key);
        if (value.Result.Revision < (parsed.Origin == "definition" ? 0 : 1)) throw new ArgumentException("Use an exact Resource revision.");
        if (value.HarnessChangeId is { } harnessId && (harnessId == Guid.Empty || value.Result.Kind != AgentDefinitionResourceKind.Knowledge)
            || value.HarnessChangeId is null && value.Before is not null) throw new ArgumentException("Use the exact Harness Resource evidence.");
        if (value.Before is { } old)
        {
            ValidateResult(old);
            if (old.Key != value.Result.Key || value.BeforeSources.SingleOrDefault(s => s.Key == old.Key)?.Fingerprint != Fingerprint(old)) throw new ArgumentException("Use the exact Harness before witness.");
        }
        ValidateResult(value.Result);
        if (!value.Result.Deleted && value.AfterSources.SingleOrDefault(s => s.Key == value.Result.Key)?.Fingerprint != Fingerprint(value.Result)) throw new ArgumentException("Use the exact Resource result witness.");
        foreach (var sources in new[] { value.BeforeSources, value.AfterSources })
            if (TenantManagementInspectionSources.Read(JsonSerializer.Serialize(sources)).Any(s => s.Kind != "resource")) throw new ArgumentException("Use Resource witnesses.");
        return value;
    }
    public static object? Metadata(TenantManagementResourceResult result) => result.Deleted ? null : new {
        key = result.Key, revision = result.Revision, enabled = result.Enabled, enabledOverride = result.EnabledOverride,
        logicalPath = result.LogicalPath, kind = result.Kind.ToString(), mediaType = result.MediaType, contentSha256 = result.ContentSha256,
        byteLength = result.ByteLength, sourceDefinitionId = result.SourceDefinitionId, sourceDefinitionVersion = result.SourceDefinitionVersion,
        sourceDefinitionResourceId = result.SourceDefinitionResourceId
    };
    public static bool CompletedChange(TenantApprovalRequest request, TenantOperationReceipt? receipt) => request is { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation.Resource: not null }
        && receipt is { Outcome: TenantOperationOutcome.Completed, ManagementResource: not null } && receipt.ReasonCode == TenantManagementMutationRules.Completed
        && receipt.RequestId == request.RequestId && receipt.OperationId == request.Operation.OperationId && receipt.TenantId == request.TenantId
        && receipt.ExecutorPrincipalId == request.ExecutorPrincipalId && receipt.InstanceId == request.Operation.InstanceId && receipt.SessionId == request.SessionId
        && receipt.RootActivationId == request.RootActivationId && receipt.ActionHash == request.Operation.ActionHash
        && JsonSerializer.Serialize(receipt.ManagementResource) == JsonSerializer.Serialize(Evidence(request.Operation.ManagementMutation!, request.Operation.ExpectedInstanceRevision));
    public static bool CompletedDeletion(TenantApprovalRequest request, TenantOperationReceipt? receipt) => request.Operation.ManagementMutation?.Resource?.Result.Deleted == true && CompletedChange(request, receipt);
    public static bool Visible(TenantApprovalRequest request, TenantManagementApprovalVisibility? visibility, long? currentRevision,
        TenantOperationReceipt? receipt, TenantWorkspaceReadApprovalVisibility? workspace, TenantAttachmentReadApprovalVisibility? attachment, TenantBrowserApprovalVisibility? browser)
    {
        if (request.Operation.ManagementMutation?.Resource is not { } plan) return true;
        var write = request.Operation.ManagementMutation.ToolName == "resources.delete" ? visibility?.ResourceDeletes : visibility?.ResourceChanges;
        bool All(TenantManagementChildVisibility? permission) => permission is { Agents: null };
        var all = All(visibility?.Resources) && All(write); var key = request.Operation.InstanceId.ToString("D") + ":" + plan.TargetKey;
        bool Has(TenantManagementChildVisibility? permission) => permission is not null && (permission.Agents is null
            || permission.Agents.Contains(request.Operation.InstanceId) || permission.Children?.Contains(key) == true);
        if (plan.CreateScope ? !all : !Has(visibility?.Resources) || !Has(write)
            || !all && currentRevision != request.Operation.ExpectedInstanceRevision && !(CompletedChange(request, receipt) && currentRevision == receipt!.InstanceRevision)) return false;
        var source = plan.Source;
        if (source is null || source.Name == "conversation:user") return true;
        if (source.Workspace is { } file)
        {
            var owner = TenantWorkspaceRules.ForCaptured(request.TenantId, file.SessionId, AgentCore.Application.Tools.TenantWorkspaceReadContract.Parse(file.ToolName, file.ArgumentsJson).Path, TenantWorkspaceRules.Context(request.Operation));
            return workspace is not null && (workspace.Sessions is null || workspace.Sessions.Contains(file.SessionId))
                && (file.Working || (file.ResourceKey is not null ? workspace.ResourceAgents is null || workspace.ResourceAgents.Contains(request.Operation.InstanceId)
                    : workspace.HomeSpaces is null || workspace.HomeSpaces.Contains(owner.OwnerId)))
                && (file.ResourceKey is { } resource ? workspace.Resources is null || workspace.Resources.Contains(TenantResourceScopes.Source(request.TenantId, request.Operation.InstanceId, resource).ResourceId!)
                    : workspace.Workspaces is null || workspace.Workspaces.Contains(TenantWorkspaceRules.Scope(owner).ResourceId!));
        }
        if (source.Attachment is { } input)
            return attachment is not null && (attachment.Sessions is null || attachment.Sessions.Contains(input.SessionId))
                && (attachment.Attachments is null || attachment.Attachments.Contains(input.AttachmentId));
        return browser is not null && (browser.ReadInstances is null || browser.ReadInstances.Contains(request.Operation.InstanceId))
            && (browser.Sessions is null || browser.Sessions.Contains(request.SessionId));
    }

    public static async ValueTask RawAsync(TenantManagementReader reader, TenantManagementMutationArguments args, IamAuthorizer iam, Guid actor, CancellationToken ct)
    {
        await TenantManagementReadRules.RawAsync(reader, iam, ct, actor == reader.Agent.PrincipalId ? null : actor).ConfigureAwait(false);
        var scopes = new List<AccessScope> { Scope(reader, args) };
        if (args.Tool == "resources.copy") scopes.Add(TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "resource", args.Value.GetProperty("key").GetString()!));
        foreach (var scope in scopes)
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get("resources.read"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("resource_read_required");
    }

    internal static TenantPreparedResourceChange Prepare(TenantManagementSource source, TenantManagementMutationArguments args,
        IReadOnlyList<AgentDefinitionPublicationResource> published, Guid allocated, DateTimeOffset at, byte[]? copiedBytes = null)
    {
        var reader = source.Reader; var owner = reader.Agent.Instance; var value = args.Value;
        var area = TenantHarnessCommandRules.IsResource(args.AuthorityTool) ? HarnessManagementScope.KnowledgeResources : HarnessManagementScope.Resources;
        if (area == HarnessManagementScope.KnowledgeResources && value.TryGetProperty("key", out var selectedKey)
            && InstanceResourceRules.Views(reader.Current, published, source.Snapshot.Resources, source.Snapshot.Skills).Single(r => r.Key == selectedKey.GetString()).Kind != AgentDefinitionResourceKind.Knowledge)
            throw new AccessDeniedException("resource_unavailable");
        if (args.Tool is "resources.set_enabled" or "resources.delete")
            return TenantAgentResourcePreparation.Choice(owner, reader.Baseline, source.Snapshot.Resources, source.Snapshot.Skills, published,
                value.GetProperty("key").GetString()!, args.Tool == "resources.delete",
                args.Tool == "resources.delete" || value.GetProperty("enabled").ValueKind == JsonValueKind.Null ? null : value.GetProperty("enabled").GetBoolean(),
                value.GetProperty("expectedResourceRevision").GetInt64(), SkillAuthor.Agent, at, scope: area);
        if (args.Tool == "resources.copy")
        {
            var key = InstanceResourceRules.ParseKey(value.GetProperty("key").GetString()!);
            var original = published.SingleOrDefault(r => r.ResourceId == key.Id) ?? throw new AccessDeniedException("resource_unavailable");
            return TenantAgentResourcePreparation.Upsert(owner, reader.Baseline, source.Snapshot.Resources, source.Snapshot.Skills, published,
                new(null, null, value.GetProperty("logicalPath").GetString()!, original.Kind, original.MediaType, null),
                copiedBytes ?? throw new AccessDeniedException("resource_content_unavailable"), SkillAuthor.Agent, allocated, at, original);
        }
        var parsed = value.TryGetProperty("key", out var target) ? InstanceResourceRules.ParseKey(target.GetString()!).Id : (Guid?)null;
        return TenantAgentResourcePreparation.Upsert(owner, reader.Baseline, source.Snapshot.Resources, source.Snapshot.Skills, published,
            new(parsed, parsed is null ? null : value.GetProperty("expectedResourceRevision").GetInt64(), value.GetProperty("logicalPath").GetString()!,
                Enum.Parse<AgentDefinitionResourceKind>(value.GetProperty("kind").GetString()!, false), value.GetProperty("mediaType").GetString()!, null),
            Encoding.UTF8.GetBytes(value.GetProperty("content").GetString()!), SkillAuthor.Agent, allocated, at, scope: area);
    }

    public static PreparedManagementMutation Build(TenantManagementSource source, TenantManagementMutationArguments args,
        IReadOnlyList<AgentDefinitionPublicationResource> published, Guid allocated, DateTimeOffset at, TenantManagementResourceSource? material, byte[]? copiedBytes = null)
    {
        var reader = source.Reader; var owner = reader.Agent.Instance;
        TenantManagementMutationRules.Eligible(reader, args.AuthorityTool);
        if (!IsTool(args.Tool) || args.ExpectedInstanceRevision != owner.Revision) throw new SecurityConflictException("stale_revision");
        if ((args.Tool == "resources.upsert") != (material is not null)) throw new AccessDeniedException("resource_source_required");
        var prepared = Prepare(source, args, published, allocated, at, copiedBytes); var mutation = prepared.Change?.Resource;
        args = TenantHarnessCommandRules.Mark(args, reader, prepared.Change);
        var after = prepared.Change is null ? owner : TenantAgentConfigurationRules.Apply(owner, TenantHarnessCommandRules.Attach(args, reader, prepared.Change), at);
        var key = args.Value.TryGetProperty("key", out var selected) ? selected.GetString() : null;
        var views = InstanceResourceRules.Views(reader.Current, published, source.Snapshot.Resources, source.Snapshot.Skills);
        var before = key is null ? null : views.Single(r => r.Key == key);
        var result = Result(prepared.Resource ?? before!, args.Tool == "resources.delete");
        var affected = new[] { key, result.Key }.Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        TenantManagementChildRead[] Witnesses(TenantManagementReader current, InstanceResourceSnapshot resources) =>
            TenantManagementInspectionSources.ResourceItems(current, published, resources, source.Snapshot.Skills).Where(i => affected.Contains(i.Key))
                .OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => new TenantManagementChildRead(i.Kind, i.Key, i.Fingerprint, i.Scope.ResourceId!)).ToArray();
        var copied = args.Tool == "resources.copy" ? published.Single(r => "definition:" + r.ResourceId.ToString("D") == key) : null;
        var plan = new PreparedManagementResource(reader.Security.Tenant.TenantId, key, Creates(args), TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(source.Snapshot.Resources)),
            at, allocated, copied, mutation?.DefinitionState, mutation?.Resource, mutation?.DeleteResourceId, mutation?.ExpectedResourceRevision, mutation?.ExpectedStateRevision,
            prepared.Change is not null, Witnesses(reader, source.Snapshot.Resources), Witnesses(reader with { Agent = reader.Agent with { Instance = after } }, prepared.Next), result, material,
            before is null ? null : Result(before, false));
        var rows = new List<TenantApprovalFieldChange> { new("Resource", before is null ? null : Result(before, false), result.Deleted ? null : result) };
        if (material is not null)
        {
            rows.Add(new("Literal text draft", null, args.Value.GetProperty("content").GetString()));
            rows.Add(new("Observed source", null, new { material.Name, material.RootRunId, material.MaterialHash, material.ToolName, material.ToolCallId,
                meaning = "This is a reviewed literal draft derived from the actual input or observation; no exact byte-copy claim is made." }));
        }
        rows.AddRange(TenantHarnessCommandRules.Review(args.Harness));
        rows.Add(new("Effect boundary", "Current Run remains pinned", "Only this Instance changes for future Runs. Resource changes grant no read, capability or execution authority."));
        var changes = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", Encoding.UTF8.GetBytes(changes));
        var value = new PreparedManagementMutation(reader.Run.SessionId, reader.Root.SessionId, args.Tool, args.Json,
            TenantManagementReadRules.ConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(after), changes, Resource: plan, Harness: args.Harness);
        TenantManagementMutationRules.Validate(value); return value;
    }

    private static TenantManagementResourceResult Result(InstanceResourceView view, bool deleted) => new(view.Key, view.Revision, view.Enabled,
        view.EnabledOverride, deleted, view.LogicalPath, view.Kind, view.MediaType, view.ContentSha256, view.ByteLength,
        view.SourceDefinitionId, view.SourceDefinitionVersion, view.SourceDefinitionResourceId);

    public static TenantAgentConfigurationChange? Change(TenantManagementSource source, PreparedManagementMutation value, IReadOnlyList<AgentDefinitionPublicationResource> published, byte[]? copiedBytes = null)
    {
        var plan = value.Resource ?? throw new AccessDeniedException("management_source_changed");
        var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson, value.Harness);
        TenantHarnessCommandRules.Revalidate(source, value, published);
        if (!TenantManagementMutationRules.Matches(value, Build(source, args, published, plan.AllocatedId, plan.PreparedAt, plan.Source, copiedBytes)))
            throw new AccessDeniedException("management_source_changed");
        var change = Prepare(source, args, published, plan.AllocatedId, plan.PreparedAt, copiedBytes).Change;
        return change is null ? null : TenantHarnessCommandRules.Attach(args, source.Reader, change);
    }
}
