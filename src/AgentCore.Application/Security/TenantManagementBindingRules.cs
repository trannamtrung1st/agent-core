using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Association-only preparation shared with the incumbent binding transaction; no credential use or secret data.
public static class TenantManagementBindingRules
{
    public static bool IsTool(string tool) => tool is "credentials.binding.propose" or "credentials.binding.remove";
    public static AccessScope Scope(TenantManagementReader reader, PreparedManagementBinding value) => TenantCredentialRules.Scope(reader.Security.Tenant.TenantId, value.Value.CredentialId);
    public static TenantManagementChildRead CredentialSource(Credential credential) => new("credential", credential.CredentialId.ToString("D"),
        TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { credential.CredentialId, credential.Revision, credential.DisplayName, credential.Kind, credential.Status, credential.Purpose })), credential.CredentialId.ToString("D"));
    private static TenantManagementChildRead BindingSource(AgentCredentialBinding binding, Credential credential) => new("binding", binding.BindingId.ToString("D"),
        TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { Binding = binding, credential.CredentialId, credential.Revision, credential.DisplayName, credential.Kind, credential.Status })), credential.CredentialId.ToString("D"));
    public static async ValueTask RawAsync(TenantManagementReader reader, Guid credential, Guid actor, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in new[] { ("agents.update", TenantManagementReadRules.Scope(reader)), ("credentials.read", TenantCredentialRules.Scope(reader.Security.Tenant.TenantId, credential)) })
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(permission == "credentials.read" ? "credential_read_required" : "agent_configuration_required");
    }
    public static IReadOnlyList<TenantManagementReadReference> Inspections(TenantManagementBindingContext context, TenantManagementMutationArguments args)
    {
        var credential = context.Credential.Value;
        var selected = args.Tool == "credentials.binding.propose" ? CredentialSource(credential)
            : BindingSource(context.Bindings.Single(b => b.BindingId == args.Value.GetProperty("bindingId").GetGuid()), credential);
        var matches = TenantManagementReadReferences.Read(context.Source.Reader.Run.Checkpoint).Where(r => r.ToolName == "configuration.inspect"
            && r.InstanceRevision == context.Source.Reader.Agent.Instance.Revision
            && TenantManagementInspectionSources.Read(r.SourcesJson).Contains(selected)).ToArray();
        if (matches.Length == 0) throw new AccessDeniedException("credential_inspection_required");
        return matches;
    }
    public static PreparedManagementMutation Build(TenantManagementBindingContext context, TenantManagementMutationArguments args, Guid id, DateTimeOffset at)
    {
        var reader = context.Source.Reader; var owner = reader.Agent.Instance; var credential = context.Credential.Value;
        TenantManagementMutationRules.Eligible(reader, args.Tool);
        if (!IsTool(args.Tool) || args.ExpectedInstanceRevision != owner.Revision || context.Credential.Owner.TenantId != reader.Security.Tenant.TenantId)
            throw new SecurityConflictException("management_source_changed");
        if (args.Tool == "credentials.binding.propose") TenantCredentialBindingRules.Bindable(credential);
        AgentCredentialBinding binding; AgentCredentialBinding? previous; var remove = args.Tool == "credentials.binding.remove";
        if (remove)
        {
            binding = previous = context.Bindings.SingleOrDefault(b => b.BindingId == args.Value.GetProperty("bindingId").GetGuid())
                ?? throw new AccessDeniedException("credential_read_required");
            if (binding.Revision != args.Value.GetProperty("expectedBindingRevision").GetInt64()) throw new SecurityConflictException("management_source_changed");
        }
        else
        {
            if (credential.CredentialId != args.Value.GetProperty("credentialId").GetGuid()) throw new AccessDeniedException("credential_read_required");
            var reference = CredentialRules.Reference(args.Value.GetProperty("reference").GetString()!);
            previous = context.Bindings.SingleOrDefault(b => b.CredentialId == credential.CredentialId || b.Reference == reference);
            if (previous is not null && (previous.CredentialId != credential.CredentialId || previous.Reference != reference)) throw new SecurityConflictException("credential_binding_conflict");
            binding = previous ?? new(id, owner.InstanceId, credential.CredentialId, reference, 1, at, at);
        }
        if (binding.AgentInstanceId != owner.InstanceId || binding.CredentialId != credential.CredentialId) throw new AccessDeniedException("credential_read_required");
        var source = CredentialSource(credential); var before = previous is null ? new[] { source } : new[] { BindingSource(previous, credential), source };
        var after = remove ? new[] { source } : new[] { BindingSource(binding, credential), source };
        var plan = new PreparedManagementBinding(binding, remove, credential.Revision, source.Fingerprint, credential.DisplayName,
            credential.Kind.ToString(), credential.Purpose.ToString(), remove || previous is null, before, after);
        var next = plan.Changed ? TenantAgentConfigurationRules.Apply(owner, new(owner.InstanceId, owner.Revision, Binding: plan), at) : owner;
        var rows = JsonSerializer.Serialize(new TenantApprovalFieldChange[] {
            new("Existing credential", null, new { credential.CredentialId, credential.DisplayName, kind = credential.Kind.ToString(), purpose = credential.Purpose.ToString(), credential.Revision }),
            new("Binding", previous is null ? null : new { previous.BindingId, previous.CredentialId, previous.Reference, previous.Revision }, remove ? null : new { binding.BindingId, binding.CredentialId, binding.Reference, binding.Revision }),
            new("Effect boundary", "Current Run remains pinned", "Only the exact existing credential association changes for future Runs. No secret, OAuth, credential use or task action is authorized.") }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var value = new PreparedManagementMutation(reader.Run.SessionId, reader.Root.SessionId, args.Tool, args.Json,
            TenantManagementReadRules.ConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(owner), TenantManagementReadRules.StableConfigurationHash(next), rows, Binding: plan);
        TenantManagementMutationRules.Validate(value); return value;
    }
    public static TenantAgentConfigurationChange Change(TenantManagementBindingContext context, PreparedManagementMutation value)
    {
        var plan = value.Binding ?? throw new AccessDeniedException("management_source_changed");
        var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson);
        if (!TenantManagementMutationRules.Matches(value, Build(context, args, plan.Value.BindingId, plan.Value.CreatedAtUtc))) throw new AccessDeniedException("management_source_changed");
        return new(context.Source.Reader.Agent.Instance.InstanceId, args.ExpectedInstanceRevision, Binding: plan);
    }
    public static void Validate(PreparedManagementMutation value)
    {
        if (IsTool(value.ToolName) != (value.Binding is not null)) throw new ArgumentException("Use one exact binding plan.");
        if (value.Binding is not { } binding) return;
        if (value.Settings is not null || value.Management is not null || value.AutomationChanges is not null || value.Resource is not null || value.Skill is not null
            || value.Harness is not null || value.Remedy is not null || value.UnattendedOfferingId is not null || value.UnattendedReasoningEffort is not null
            || binding.Value.BindingId == Guid.Empty || binding.Value.CredentialId == Guid.Empty || binding.Value.AgentInstanceId == Guid.Empty
            || binding.Value.Reference != CredentialRules.Reference(binding.Value.Reference) || binding.Value.Revision < 1 || binding.CredentialRevision < 1
            || binding.Remove != (value.ToolName == "credentials.binding.remove") || !TenantMemoryOperationRules.ValidHash(binding.CredentialFingerprint)
            || binding.DisplayName != CredentialRules.DisplayName(binding.DisplayName) || !Enum.TryParse<CredentialKind>(binding.CredentialKind, out var kind) || !Enum.IsDefined(kind) || kind.ToString() != binding.CredentialKind
            || !Enum.TryParse<CredentialPurpose>(binding.CredentialPurpose, out var purpose) || !Enum.IsDefined(purpose) || purpose.ToString() != binding.CredentialPurpose || purpose == CredentialPurpose.ContainerRegistry || binding.Remove && !binding.Changed)
            throw new ArgumentException("Use exact safe binding metadata and revisions.");
        _ = TenantManagementInspectionSources.Read(JsonSerializer.Serialize(binding.BeforeSources));
        _ = TenantManagementInspectionSources.Read(JsonSerializer.Serialize(binding.AfterSources));
        if (binding.BeforeSources.Count > 2 || binding.AfterSources.Count > 2 || binding.BeforeSources.Concat(binding.AfterSources).Any(s => s.Kind is not ("credential" or "binding") || s.ResourceId != binding.Value.CredentialId.ToString("D")))
            throw new ArgumentException("Use only exact binding/credential witnesses.");
        if (!binding.Remove) TenantCredentialBindingRules.NewBinding(binding.Value);
    }
}
