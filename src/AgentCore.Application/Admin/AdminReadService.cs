using System.Diagnostics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AdminReadService(
    IAgentDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns,
    IAgentDefinitionAdminStore adminStore,
    IAgentInstanceStore instances,
    IModelCatalog catalog,
    IToolConfigurationGate configurationGate,
    IBrowser? browser = null, Execution.AgentRunConfigurationResolver? configurations = null)
{
    public const int MaxInventoryItems = 256;

    public async ValueTask<IReadOnlyList<AdminDefinitionInventoryItem>> ListDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var drafts = await adminStore.ListDraftsAsync(cancellationToken).ConfigureAwait(false);
        var draftCounts = drafts
            .GroupBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var items = new List<AdminDefinitionInventoryItem>();
        foreach (var definition in await builtIns.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(MapBuiltIn(definition, CountFor(draftCounts, definition.Id)));
        }

        foreach (var summary in await adminStore.ListPublicationsAsync(cancellationToken: cancellationToken)
                     .ConfigureAwait(false))
        {
            var publication = await adminStore
                .GetPublicationAsync(summary.DefinitionId, summary.Version, cancellationToken)
                .ConfigureAwait(false);
            if (publication is null)
            {
                continue;
            }

            items.Add(MapDurable(publication, CountFor(draftCounts, publication.DefinitionId)));
        }

        var represented = new HashSet<string>(items.Select(item => item.DefinitionId), StringComparer.Ordinal);
        foreach (var group in drafts.GroupBy(item => item.DefinitionId, StringComparer.Ordinal))
        {
            if (represented.Contains(group.Key))
            {
                continue;
            }

            var latest = group.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.DraftId).First();
            var draft = await adminStore.GetDraftAsync(latest.DraftId, cancellationToken).ConfigureAwait(false);
            items.Add(new AdminDefinitionInventoryItem(
                group.Key,
                Version: 0,
                AdminDefinitionSources.Draft,
                AdminDefinitionStatuses.DraftOnly,
                draft?.Candidate.Identity.Name ?? group.Key,
                group.Count()));
        }

        return items
            .OrderBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .Take(MaxInventoryItems)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<AdminInstanceInventoryItem>> ListInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await instances.ListAsync(MaxInventoryItems, cancellationToken).ConfigureAwait(false);
        return rows
            .OrderBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId)
            .Select(MapInstance)
            .ToArray();
    }

    public async ValueTask<AdminEffectiveConfiguration> GetEffectiveConfigurationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            OperationalDiagnostics.RecordAdmin(
                "resolve", "rejected", "notFound", started, null, null, instanceId, null);
            throw AgentCoreErrors.NotFound("Agent instance was not found.");
        }

        var definition = await definitions
            .GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            OperationalDiagnostics.RecordAdmin(
                "resolve",
                "rejected",
                "notFound",
                started,
                instance.DefinitionId,
                instance.ActiveVersion,
                instance.InstanceId,
                null);
            throw AgentCoreErrors.NotFound(
                $"Agent '{instance.DefinitionId}' version {instance.ActiveVersion} was not found.");
        }

        var (source, status) = await ResolveDefinitionMetadataAsync(
                instance.DefinitionId,
                instance.ActiveVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (configurations is not null) definition = (await configurations.ResolveAsync(instanceId, cancellationToken, allowArchived: true)).Configuration.Definition;
        var resolved = AdminEffectiveConfigurationResolver.Resolve(
            configurations is null ? instance : instance with { SettingsOverrides = null },
            definition,
            catalog,
            configurationGate,
            source,
            status);
        resolved = resolved with { Browser = browser is null ? null : new BrowserEffectiveConfiguration(
            browser.Provider.ProviderId, browser.Provider.DisplayName, browser.HostPolicy.Enabled,
            configurationGate.IsConfigured(ToolCatalog.BrowserNavigate), browser.HostPolicy.ProfileMode.ToString(),
            browser.HostPolicy.PolicyMode.ToString(), browser.Provider.SupportedFeatures.Select(f => f.ToString()).Order(StringComparer.Ordinal).ToArray(),
            browser.HostPolicy.Limits.SnapshotBytes, browser.HostPolicy.Limits.CaptureBytes, browser.HostPolicy.Limits.DownloadBytes, browser.Provider.Engine, browser.HostPolicy.Limits) };
        OperationalDiagnostics.RecordAdmin(
            "resolve",
            "completed",
            "completed",
            started,
            definition.Id,
            definition.Version,
            instance.InstanceId,
            "resolved");
        return resolved;
    }

    private async ValueTask<(string Source, string Status)> ResolveDefinitionMetadataAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken)
    {
        if (await builtIns.GetAsync(definitionId, version, cancellationToken).ConfigureAwait(false) is not null)
        {
            return (AdminDefinitionSources.BuiltIn, AdminDefinitionStatuses.Published);
        }

        var publication = await adminStore.GetPublicationAsync(definitionId, version, cancellationToken)
            .ConfigureAwait(false);
        if (publication is not null)
        {
            return (
                AdminDefinitionSources.Durable,
                publication.Status == DefinitionPublicationStatus.Deprecated
                    ? AdminDefinitionStatuses.Deprecated
                    : AdminDefinitionStatuses.Published);
        }

        return (AdminDefinitionSources.BuiltIn, AdminDefinitionStatuses.Published);
    }

    private static int CountFor(IReadOnlyDictionary<string, int> draftCounts, string definitionId) =>
        draftCounts.TryGetValue(definitionId, out var count) ? count : 0;

    private static AdminDefinitionInventoryItem MapBuiltIn(AgentDefinition definition, int draftCount) =>
        new(
            definition.Id,
            definition.Version,
            AdminDefinitionSources.BuiltIn,
            AdminDefinitionStatuses.Published,
            definition.Identity.Name,
            draftCount);

    private static AdminDefinitionInventoryItem MapDurable(AgentDefinitionPublication publication, int draftCount) =>
        new(
            publication.DefinitionId,
            publication.Version,
            AdminDefinitionSources.Durable,
            publication.Status == DefinitionPublicationStatus.Deprecated
                ? AdminDefinitionStatuses.Deprecated
                : AdminDefinitionStatuses.Published,
            publication.Payload.Identity.Name,
            draftCount);

    private static AdminInstanceInventoryItem MapInstance(AgentInstance instance) =>
        new(
            instance.InstanceId,
            instance.DefinitionId,
            instance.ActiveVersion,
            instance.Lifecycle,
            instance.Persona.Name,
            instance.CreatedAt,
            instance.UpdatedAt);
}

public static class AdminDefinitionSources
{
    public const string BuiltIn = "builtIn";
    public const string Durable = "durable";
    public const string Draft = "draft";
}

public static class AdminDefinitionStatuses
{
    public const string Published = "published";
    public const string Deprecated = "deprecated";
    public const string DraftOnly = "draftOnly";
}

public sealed record AdminDefinitionInventoryItem(
    string DefinitionId,
    int Version,
    string Source,
    string Status,
    string DisplayName,
    int DraftCount = 0);

public sealed record AdminInstanceInventoryItem(
    Guid InstanceId,
    string DefinitionId,
    int ActiveVersion,
    AgentInstanceLifecycle Lifecycle,
    string PersonaName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminKnowledgeSource(
    string Identity,
    string Title,
    string Citation,
    string ResolvedResourcePath);

public sealed record AdminEffectiveModel(
    string CatalogKey,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ModelId);

public sealed record AdminDurableExecutionEligibility(
    bool InstanceActive,
    bool DefinitionResolved,
    bool TriggerPolicyEnabled,
    bool AllowsScheduleSource,
    bool AllowsApplicationEventSource,
    bool CanAcceptNewTriggeredWork);

public sealed record AdminEffectiveConfiguration(
    string DefinitionSource,
    string DefinitionId,
    int DefinitionVersion,
    string DefinitionStatus,
    Guid InstanceId,
    string InstanceLifecycle,
    long InstanceRevision,
    long PersonaRevision,
    AgentIdentity Persona,
    ProviderPreferences ProviderPreferences,
    AdminEffectiveModel EffectiveModel,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<string> HarnessReferences,
    string? WorkspaceTemplateId,
    IReadOnlyList<AdminKnowledgeSource> KnowledgeSources,
    MemoryPolicy MemoryPolicy,
    TriggerPolicy? TriggerPolicy,
    AdminDurableExecutionEligibility DurableExecutionEligibility,
    string? UnattendedModelCatalogKey = null,
    string? UnattendedReasoningEffort = null,
    BrowserEffectiveConfiguration? Browser = null);

public sealed record BrowserEffectiveConfiguration(string ProviderId, string DisplayName, bool Enabled, bool Ready, string ProfileMode, string PolicyMode, IReadOnlyList<string> SupportedFeatures, int MaxSnapshotBytes, int MaxCaptureBytes, int MaxDownloadBytes, string Engine = "unknown", BrowserOperationalLimits? Limits = null);
