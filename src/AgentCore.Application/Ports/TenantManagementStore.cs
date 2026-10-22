using AgentCore.Application.Tools;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
namespace AgentCore.Application.Ports;

public sealed record TenantManagementReader(AgentRun Run, AgentRun Root, TenantSecurityState Security, TenantAgent Agent, AgentDefinition Baseline, AgentDefinition Current,
    TenantManagementInspectionCatalog? Inspection = null);
public sealed record TenantManagementSource(TenantManagementReader Reader, TenantAgentConfigurationSnapshot Snapshot);
public sealed record TenantManagementResourceContext(TenantManagementSource Source,
    IReadOnlyList<AgentDefinitionPublicationResource> Published, TenantManagementResourceSource? Material, TenantManagementMutationArguments? Arguments = null);
public sealed record TenantResourcePinRetention(long ResourceRevision, bool Deleted, long InstanceRevision);
public sealed record TenantManagementSourceRetention(string? CurrentSourcesJson, IReadOnlySet<string> DeletedSources);
public sealed record TenantManagementReadObservation(TenantManagementReader Reader, PreparedManagementRead Value, bool SourcesChanged = false,
    TenantManagementSourceRetention? Retention = null);
public sealed record TenantManagementCommandCommit(PreparedTenantOperation Operation, Guid RunId, Guid Generation,
    long ExpectedRunRevision, string SourceCallId, string ParametersHash);
// Management facet of the existing tenant Instance/IAM/Run aggregate, not another configuration owner.
public interface ITenantManagementStore
{
    ValueTask<TenantResourcePinRetention?> ReadRetainedResourcePinAsync(TenantRunReference reference, EffectiveAgentResource resource, CancellationToken ct = default);
    ValueTask<TenantManagementResourceContext> ReadTenantManagementResourceContextAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TryCommitManagementCommandAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantManagementCommandCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantManagementSource> ReadTenantManagementSourceAsync(TenantRunReference reference, Guid? generation, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask RequireTenantManagementSkillSourcesAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask<TenantManagementReadObservation> ObserveTenantManagementReadAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, PreparedManagementRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask RequireTenantManagementReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantManagementReadReference> sources,
        Guid? generation, CancellationToken ct = default);
}
