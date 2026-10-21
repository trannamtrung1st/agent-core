using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantExperienceSourceReference(Guid TenantId, TenantExperienceSourceWitness Witness);
public sealed record TenantExperienceFactSource(Guid TenantId, Guid ExperienceId, string ContentHash, long Revision);
public sealed record TenantExperienceSourceObservation(TenantMemoryReader Reader, TenantIsolationMetadata Settings,
    TenantExperienceSourceWitness Witness, string EvidenceJson, string Title);
public sealed record TenantExperienceObservation(TenantMemoryReader Reader, TenantIsolationMetadata Settings,
    IReadOnlyList<TenantExperience> Items, long SecurityRevision, bool ReadAllowed);
public sealed record TenantExperienceFactObservation(TenantMemoryReader Reader, TenantIsolationMetadata Settings, IReadOnlyList<TenantExperience> Items, PreparedExperienceRead Value, bool HasMore, bool SourcesChanged = false);
public sealed record TenantExperienceOperationObservation(TenantMemoryReader Reader, TenantIsolationMetadata Settings,
    TenantExperienceArguments Arguments, TenantExperienceSourceObservation? Source, IReadOnlyList<TenantExperience> Items,
    PreparedExperienceOperation Value, TenantExperience? ExistingResult = null, bool SourcesChanged = false);

// The owning IAM store repeats raw reads, actual-root ownership and source/value CAS in its transaction.
public sealed record TenantExperienceCommit(Guid OperationId, Guid RunId, Guid Generation, long ExpectedRunRevision,
    string SourceCallId, string ParametersHash, PreparedExperienceOperation Prepared, long ExpectedInstanceRevision, string ActionHash);

public interface ITenantExperienceStore : ITenantDirectOperationStore
{
    ValueTask<TenantExperienceSourceObservation> ObserveTenantExperienceSourceAsync(TenantRunReference reference, Guid? generation,
        ExperienceSourceKind kind, Guid id, TenantExperienceSourceWitness? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask<TenantExperienceOperationObservation> ObserveTenantExperienceOperationAsync(TenantRunReference reference, Guid? generation,
        TenantExperienceArguments arguments, PreparedExperienceOperation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask<TenantExperienceFactObservation> ObserveTenantExperienceFactsAsync(TenantRunReference reference, Guid? generation, TenantContinuityArguments arguments,
        PreparedExperienceRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask<TenantExperienceObservation> ReadTenantRunExperienceAsync(TenantRuntimeFrame frame, string? query = null,
        Guid? experienceId = null, int limit = 5, CancellationToken ct = default);
    ValueTask RequireTenantExperienceSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantExperienceSourceReference> sources,
        IReadOnlyList<TenantExperienceFactSource> facts, Guid? generation, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TryCommitTenantExperienceAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantExperienceCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
}

public sealed record TenantExperienceQuery(int Page = 1, int PageSize = 25, Guid? ExperienceId = null, bool IncludeHistorical = false);
public sealed record TenantExperienceItemView(TenantExperience Experience, bool CanManage);
public sealed record TenantExperiencePage(IReadOnlyList<TenantExperienceItemView> Items, int Total, int RetainedCount,
    long SecurityRevision, int Page, int PageSize, string AgentName, bool CanReset);
public sealed record TenantExperienceChange(long ExpectedSecurityRevision, Guid? ExperienceId = null, long? ExpectedExperienceRevision = null,
    ExperienceVisibility Visibility = ExperienceVisibility.Deleted, int? ExpectedRetainedCount = null);
public sealed record TenantExperienceChangeResult(int Changed, long SecurityRevision);
public interface ITenantExperienceManagementStore
{
    ValueTask<TenantExperiencePage> ReadOwnTenantExperienceAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceQuery query, CancellationToken ct = default);
    ValueTask<TenantExperienceChangeResult?> TryChangeOwnTenantExperienceAsync(Guid tenant, Guid agent, HumanSessionReference human,
        TenantExperienceChange change, AuthorizationAudit audit, CancellationToken ct = default);
}
