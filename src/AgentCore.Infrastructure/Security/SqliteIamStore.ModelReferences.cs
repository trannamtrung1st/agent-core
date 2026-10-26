using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Models;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private sealed class ModelReferenceSqlRow
    {
        public string ResourceId { get; set; } = "";
        public string ReferenceId { get; set; } = "";
        public string OfferingId { get; set; } = "";
        public string Label { get; set; } = "";
        public string Usage { get; set; } = "";
        public long Revision { get; set; }
        public string State { get; set; } = "";
    }
    public async ValueTask<ModelReferencePage> ReadModelReferencesAsync(ModelReferenceQuery query, CancellationToken ct = default)
    {
        ModelReferenceQueries.Validate(query);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == query.TenantId, ct);
        if (tenant?.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        var exists = query.OfferingId is { } offering
            ? await db.Set<ModelOfferingRecord>().AnyAsync(o => o.TenantId == query.TenantId && o.OfferingId == offering, ct)
            : await db.Set<ProviderConnectionRecord>().AnyAsync(c => c.TenantId == query.TenantId && c.ConnectionId == query.ConnectionId, ct);
        if (!exists) throw new AccessDeniedException("resource_unavailable");
        // Every statement is fixed SQL selected by a closed enum. IDs/visibility/paging are bound parameters.
        const string offerings = """
            WITH selected_models AS (
                SELECT lower(OfferingId) AS OfferingId FROM ModelOfferings
                WHERE lower(TenantId) = $tenant AND ($offering IS NULL OR lower(OfferingId) = $offering)
                AND ($connection IS NULL OR lower(ConnectionId) = $connection)
                AND ($offerings IS NULL OR lower(OfferingId) IN (SELECT value FROM json_each($offerings))))
            """;
        var source = query.Kind switch
        {
            ModelReferenceKind.Default => """
                SELECT lower(s.TenantId) AS ResourceId, 'default' AS ReferenceId, lower(s.DefaultOfferingId) AS OfferingId,
                  'Organization default' AS Label, 'Organization default' AS Usage, s.Revision, 'Configured' AS State,
                  NULL AS AgentId, NULL AS SessionId
                FROM TenantModelSettings s WHERE lower(s.TenantId) = $tenant
                """,
            ModelReferenceKind.Definitions => """
                SELECT d.DefinitionId AS ResourceId, d.DraftId AS ReferenceId,
                  CASE WHEN json_extract(d.CandidateJson,'$.modelDefaults.mode')='auto' THEN NULL ELSE COALESCE(json_extract(d.CandidateJson,'$.modelDefaults.offeringId'), (SELECT lower(DefaultOfferingId) FROM TenantModelSettings WHERE lower(TenantId) = $tenant)) END AS OfferingId,
                  COALESCE(json_extract(d.CandidateJson,'$.identity.name'), d.DefinitionId) AS Label,
                  CASE WHEN json_extract(d.CandidateJson,'$.modelDefaults.offeringId') IS NULL THEN 'Draft inherits organization default' ELSE 'Definition draft' END AS Usage, d.Revision, 'Draft' AS State, NULL AS AgentId, NULL AS SessionId
                FROM AgentDefinitionDrafts d JOIN TenantAgentDefinitions o ON o.DefinitionId = d.DefinitionId
                WHERE lower(o.TenantId) = $tenant AND o.DeletedAt IS NULL
                UNION ALL
                SELECT p.DefinitionId, 'v' || p.Version, CASE WHEN json_extract(p.PayloadJson,'$.modelDefaults.mode')='auto' THEN NULL ELSE COALESCE(json_extract(p.PayloadJson,'$.modelDefaults.offeringId'), (SELECT lower(DefaultOfferingId) FROM TenantModelSettings WHERE lower(TenantId) = $tenant)) END,
                  COALESCE(json_extract(p.PayloadJson,'$.identity.name'), p.DefinitionId),
                  'Published Definition v' || p.Version || CASE WHEN json_extract(p.PayloadJson,'$.modelDefaults.offeringId') IS NULL THEN ' inherits organization default' ELSE '' END, p.MetadataRevision, CASE p.Status WHEN 0 THEN 'Published' ELSE 'Deprecated' END, NULL, NULL
                FROM AgentDefinitionPublications p JOIN TenantAgentDefinitions o ON o.DefinitionId = p.DefinitionId
                WHERE lower(o.TenantId) = $tenant AND o.DeletedAt IS NULL
                """,
            ModelReferenceKind.Instances => """
                SELECT a.InstanceId AS ResourceId, 'conversation' AS ReferenceId,
                  CASE WHEN json_type(a.SettingsOverridesJson,'$.modelPreference.value')='null' THEN (SELECT lower(DefaultOfferingId) FROM TenantModelSettings WHERE lower(TenantId) = $tenant)
                    WHEN json_extract(a.SettingsOverridesJson,'$.modelPreference.value.mode')='fixed' THEN json_extract(a.SettingsOverridesJson,'$.modelPreference.value.offeringId')
                    WHEN json_extract(a.SettingsOverridesJson,'$.modelPreference.value.mode')='auto' OR json_extract(p.PayloadJson,'$.modelDefaults.mode')='auto' THEN NULL
                    ELSE COALESCE(json_extract(p.PayloadJson,'$.modelDefaults.offeringId'), (SELECT lower(DefaultOfferingId) FROM TenantModelSettings WHERE lower(TenantId) = $tenant)) END AS OfferingId,
                  COALESCE(json_extract(a.PersonaJson,'$.name'),a.InstanceId) AS Label,
                  CASE WHEN json_extract(a.SettingsOverridesJson,'$.modelPreference.value.offeringId') IS NOT NULL THEN 'Instance conversation override'
                    WHEN json_type(a.SettingsOverridesJson,'$.modelPreference.value') IS NOT 'null' AND json_extract(p.PayloadJson,'$.modelDefaults.offeringId') IS NOT NULL
                    THEN 'Inherited Definition default' ELSE 'Inherited organization default' END AS Usage,
                  a.Revision, a.Lifecycle AS State, NULL AS AgentId, NULL AS SessionId
                FROM AgentInstances a JOIN SystemIdentities i ON i.AgentInstanceId = a.InstanceId
                LEFT JOIN AgentDefinitionPublications p ON p.DefinitionId = a.DefinitionId AND p.Version = a.ActiveVersion
                WHERE lower(i.TenantId) = $tenant AND i.Kind = $agentKind AND i.Status <> $removed
                UNION ALL
                SELECT a.InstanceId, 'unattended', json_extract(a.UnattendedModelJson,'$.offeringId'),
                  COALESCE(json_extract(a.PersonaJson,'$.name'),a.InstanceId), 'Unattended preference', a.Revision, a.Lifecycle, NULL, NULL
                FROM AgentInstances a JOIN SystemIdentities i ON i.AgentInstanceId = a.InstanceId
                WHERE lower(i.TenantId) = $tenant AND i.Kind = $agentKind AND i.Status <> $removed
                """,
            ModelReferenceKind.Automations => """
                SELECT a.AutomationId AS ResourceId, 'override' AS ReferenceId, json_extract(a.ModelPreferenceJson,'$.OfferingId') AS OfferingId,
                  a.Name AS Label, 'Automation override' AS Usage, a.Revision, CAST(a.Status AS TEXT) AS State,
                  a.AgentInstanceId AS AgentId, NULL AS SessionId
                FROM Automations a WHERE lower(a.TenantId) = $tenant
                """,
            ModelReferenceKind.Sessions => """
                SELECT s.SessionId AS ResourceId, 'selection' AS ReferenceId, x.ModelOfferingId AS OfferingId,
                  s.Title AS Label, 'Conversation selection' AS Usage, s.Revision, s.Status AS State, NULL AS AgentId, NULL AS SessionId
                FROM Sessions s JOIN TenantSessions o ON o.SessionId = s.SessionId JOIN SessionSnapshots x ON x.SessionId = s.SessionId
                WHERE lower(o.TenantId) = $tenant AND s.DurablyDeletedAtUtc IS NULL
                UNION ALL
                SELECT s.SessionId, 'provenance', h.OfferingId, s.Title, 'Retained summary/message provenance', s.Revision, s.Status, NULL, NULL
                FROM Sessions s JOIN TenantSessions o ON o.SessionId = s.SessionId
                JOIN (SELECT SessionId, SummaryModelOfferingId AS OfferingId FROM SessionSnapshots
                      UNION SELECT SessionId, ModelOfferingId FROM ConversationEntries) h ON h.SessionId = s.SessionId
                WHERE lower(o.TenantId) = $tenant AND s.DurablyDeletedAtUtc IS NULL
                """,
            ModelReferenceKind.Runs => """
                SELECT r.AgentRunId AS ResourceId, r.AgentRunId AS ReferenceId,
                  json_extract(r.PayloadJson,'$.pinnedModel.offeringId') AS OfferingId, s.Title AS Label,
                  'Admitted Run pin' AS Usage, r.Revision, CAST(r.Status AS TEXT) AS State,
                  r.AgentInstanceId AS AgentId, r.SessionId AS SessionId
                FROM AgentRuns r JOIN TenantSessions o ON o.SessionId = r.SessionId AND o.TenantId = r.TenantId
                  JOIN Sessions s ON s.SessionId = r.SessionId
                WHERE lower(r.TenantId) = $tenant AND s.DurablyDeletedAtUtc IS NULL
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };
        var filtered = "SELECT * FROM (" + source + """
            ) refs WHERE OfferingId IN (SELECT OfferingId FROM selected_models)
            AND ($resources IS NULL OR lower(ResourceId) IN (SELECT value FROM json_each($resources)))
            AND ($agents IS NULL OR AgentId IS NULL OR lower(AgentId) IN (SELECT value FROM json_each($agents)))
            AND ($sessions IS NULL OR SessionId IS NULL OR lower(SessionId) IN (SELECT value FROM json_each($sessions)))
            """;
        object[] Parameters() => [new SqliteParameter("$tenant", query.TenantId.ToString("D")),
            new SqliteParameter("$offering", (object?)query.OfferingId?.ToString("D") ?? DBNull.Value),
            new SqliteParameter("$connection", (object?)query.ConnectionId?.ToString("D") ?? DBNull.Value),
            new SqliteParameter("$offerings", Ids(query.Visible.Offerings)), new SqliteParameter("$resources", Ids(query.Visible.Resources)),
            new SqliteParameter("$agents", Ids(query.Visible.Agents)), new SqliteParameter("$sessions", Ids(query.Visible.Sessions)),
            new SqliteParameter("$agentKind", (object)(int)SystemIdentityKind.Agent), new SqliteParameter("$removed", (object)(int)IdentityStatus.Removed)];
        // Only the fixed clauses above are concatenated; every query/caller value is a named parameter.
#pragma warning disable EF1003
        var total = await db.Database.SqlQueryRaw<int>(offerings + " SELECT COUNT(*) AS Value FROM (" + filtered + ")", Parameters()).SingleAsync(ct);
        var rows = await db.Database.SqlQueryRaw<ModelReferenceSqlRow>(offerings
            + " SELECT ResourceId, ReferenceId, OfferingId, Label, Usage, Revision, State FROM (" + filtered
            + ") ORDER BY ResourceId, ReferenceId, OfferingId LIMIT $size OFFSET $offset", [.. Parameters(),
                new SqliteParameter("$size", query.PageSize), new SqliteParameter("$offset", checked(query.Page * query.PageSize))]).ToArrayAsync(ct);
#pragma warning restore EF1003
        await transaction.CommitAsync(ct);
        return new(rows.Select(r => new ModelReferenceItem(Guid.Parse(r.ResourceId), r.ReferenceId, Guid.Parse(r.OfferingId),
            r.Label, r.Usage, r.Revision, ModelReferenceQueries.State(query.Kind, r.State))).ToArray(), total, query.Page, query.PageSize, tenant.Revision);
    }
    private static object Ids(IReadOnlyList<Guid>? ids) => ids is null ? DBNull.Value : JsonSerializer.Serialize(ids.Select(id => id.ToString("D")));
}
