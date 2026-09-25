using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

/// <summary>
/// Runtime catalog over immutable built-in file definitions and durable Admin publications.
/// </summary>
public sealed class CompositeAgentDefinitionStore(
    FileAgentDefinitionStore builtIns,
    IAgentDefinitionAdminStore admin) : IAgentDefinitionStore
{
    public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        var builtin = await builtIns.ListAsync(cancellationToken).ConfigureAwait(false);
        var summaries = await admin.ListPublicationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var durable = new List<AgentDefinition>();
        foreach (var summary in summaries)
        {
            var publication = await admin.GetPublicationAsync(summary.DefinitionId, summary.Version, cancellationToken)
                .ConfigureAwait(false);
            if (publication is not null)
            {
                durable.Add(publication.Payload);
            }
        }

        return builtin.Concat(durable).ToArray();
    }

    public async ValueTask<AgentDefinition?> GetAsync(
        string id,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        if (version is { } exact)
        {
            var builtin = await builtIns.GetAsync(id, exact, cancellationToken).ConfigureAwait(false);
            if (builtin is not null)
            {
                return builtin;
            }

            var durable = await admin.GetPublicationAsync(id, exact, cancellationToken).ConfigureAwait(false);
            return durable?.Payload;
        }

        var builtinLatest = await builtIns.GetAsync(id, version: null, cancellationToken).ConfigureAwait(false);
        var durableSummaries = await admin.ListPublicationsAsync(id, cancellationToken).ConfigureAwait(false);
        AgentDefinition? bestDurable = null;
        foreach (var summary in durableSummaries)
        {
            if (summary.Status == DefinitionPublicationStatus.Deprecated)
            {
                continue;
            }

            var publication = await admin.GetPublicationAsync(id, summary.Version, cancellationToken)
                .ConfigureAwait(false);
            if (publication is null)
            {
                continue;
            }

            if (bestDurable is null || publication.Version > bestDurable.Version)
            {
                bestDurable = publication.Payload;
            }
        }

        if (builtinLatest is null)
        {
            return bestDurable;
        }

        if (bestDurable is null)
        {
            return builtinLatest;
        }

        return builtinLatest.Version >= bestDurable.Version ? builtinLatest : bestDurable;
    }
}
