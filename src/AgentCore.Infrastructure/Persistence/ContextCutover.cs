using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

/// <summary>Explicit, one-way maintenance operation. Never runs from normal startup.</summary>
public static class ContextCutover
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string Recovery = "Stop the application, back up its data roots, then run: dotnet run --project src/AgentCore.Api -- migrate-context <database-path> <new-backup-path>. Drain or explicitly cancel nonterminal Runs using the previous version first. No data was deleted or tools replayed.";

    public static async Task ValidateAsync(AgentCoreDbContext db, CancellationToken ct = default)
    {
        // Inspect raw shapes before any current-schema deserializer can read retained configuration.
        foreach (var json in await db.Sessions.Select(r => r.DefinitionJson).ToArrayAsync(ct)) EnsureDefinition(json);
        foreach (var json in await db.AgentDefinitionPublications.Select(r => r.PayloadJson).ToArrayAsync(ct)) EnsureDefinition(json);
        foreach (var json in await db.AgentDefinitionDrafts.Select(r => r.CandidateJson).ToArrayAsync(ct)) EnsureDefinition(json);
        foreach (var json in await db.AgentInstances.Select(r => r.SettingsOverridesJson).ToArrayAsync(ct))
            if (json is not null && JsonNode.Parse(json) is JsonObject obj && obj.ContainsKey("systemInstructions"))
                throw new InvalidOperationException("Instance settings require the context cutover. " + Recovery);
        foreach (var row in await db.AgentRuns.AsNoTracking().ToArrayAsync(ct))
        {
            var data = JsonNode.Parse(row.PayloadJson)!;
            if (data["admission"]?["configuration"]?["definition"] is JsonObject definition) EnsureDefinition(definition.ToJsonString());
            if (!Terminal(row.Status) && data["checkpoint"] is JsonObject checkpoint)
            {
                var value = checkpoint.Deserialize<AgentRunCheckpoint>(Json)!;
                AgentRunToolCallCheckpoint.EnsureCurrentContextContract(value, data["admission"]?["responseId"]?.GetValue<Guid>());
            }
        }
    }

    private static bool Terminal(int status) => (AgentRunStatus)status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled;
    private static void EnsureDefinition(string json)
    {
        var value = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Invalid retained Definition JSON.");
        if (value.ContainsKey("systemInstructions") || value["schemaVersion"]?.GetValue<int>() != 2 || value["agentInstructions"] is null)
            throw new InvalidOperationException("Stored Definition requires the three-layer context cutover. " + Recovery);
    }

    public static async Task<int> MigrateAsync(string databasePath, string backupPath, CancellationToken ct = default)
    {
        databasePath = Path.GetFullPath(databasePath); backupPath = Path.GetFullPath(backupPath);
        if (!File.Exists(databasePath) || databasePath == backupPath || File.Exists(backupPath))
            throw new ArgumentException("Use an existing stopped database and a new, distinct backup path.");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(new SqliteConnectionStringBuilder {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options;
        await using var db = new AgentCoreDbContext(options);
        await db.Database.OpenConnectionAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        // SQLite's backup API includes committed WAL content and does not require destructive file copying.
        await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
        { await backup.OpenAsync(ct); connection.BackupDatabase(backup); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var runs = await db.AgentRuns.ToListAsync(ct);
        foreach (var run in runs.Where(r => !Terminal(r.Status)))
        {
            var payload = JsonNode.Parse(run.PayloadJson)!;
            if (payload["admission"]?["configuration"]?["definition"]?["schemaVersion"]?.GetValue<int>() != 2
                || payload["checkpoint"] is { } checkpoint && !CurrentCheckpoint(checkpoint))
                throw new InvalidOperationException("Nonterminal Run " + run.AgentRunId + " must be drained or explicitly cancelled before cutover. " + Recovery);
        }
        var count = 0;
        var publications = await db.AgentDefinitionPublications.ToListAsync(ct);
        foreach (var row in publications) { var json = Definition(row.PayloadJson); if (json != row.PayloadJson) { row.PayloadJson = json; count++; } }
        var drafts = await db.AgentDefinitionDrafts.ToListAsync(ct);
        foreach (var row in drafts) { var json = Definition(row.CandidateJson, candidate: true); if (json != row.CandidateJson) { row.CandidateJson = json; count++; } }
        var sessions = await db.Sessions.Include(r => r.Snapshot).Include(r => r.Entries).ToListAsync(ct);
        foreach (var row in sessions) { var json = Definition(row.DefinitionJson); if (json != row.DefinitionJson) { row.DefinitionJson = json; count++; } }
        foreach (var row in await db.AgentInstances.ToListAsync(ct))
        {
            if (row.SettingsOverridesJson is null) continue;
            var obj = JsonNode.Parse(row.SettingsOverridesJson)!.AsObject();
            if (Rename(obj)) { row.SettingsOverridesJson = obj.ToJsonString(Json); count++; }
            _ = JsonSerializer.Deserialize<InstanceSettingsOverrides>(row.SettingsOverridesJson, Json) ?? throw new InvalidOperationException("Invalid retained Instance settings.");
        }
        var activations = await db.Activations.ToDictionaryAsync(r => r.ActivationId, ct);
        foreach (var row in runs)
        {
            var originalPayload = row.PayloadJson;
            var originalHash = activations[row.ActivationId].AdmissionHash;
            var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
            if (payload["admission"]?["configuration"] is not JsonObject configuration) continue;
            var definition = configuration["definition"] ?? throw new InvalidOperationException("Run configuration is missing its Definition.");
            configuration["definition"] = JsonNode.Parse(Definition(definition.ToJsonString()));
            // Re-materialize with the new constructor so ConfigurationHash uses the same canonical algorithm as admission.
            var pinned = configuration.Deserialize<AgentRunConfiguration>(Json) ?? throw new InvalidOperationException("Invalid pinned Run configuration.");
            configuration["configurationHash"] = pinned.ConfigurationHash;
            configuration["fieldSources"] ??= new JsonObject(); // Historical per-field sources cannot be invented.
            row.PayloadJson = payload.ToJsonString(Json);
            var run = AgentRunStoreMapping.ToDomain(row);
            var session = sessions.Single(s => s.SessionId == row.SessionId);
            activations[row.ActivationId].AdmissionHash = AgentRunStoreMapping.AdmissionHash(SqliteMemoryStore.ToSnapshot(session, session.Entries), run);
            if (originalPayload != row.PayloadJson || originalHash != activations[row.ActivationId].AdmissionHash) count++;
        }
        // Evaluation fingerprints intentionally remain historical. The new candidate fingerprint invalidates old publish gates.
        // Management history remains inert audit evidence; replay is separately fenced by current-schema validation.
        await db.SaveChangesAsync(ct);
        await ValidateAsync(db, ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    private static bool CurrentCheckpoint(JsonNode node)
    { try { AgentRunToolCallCheckpoint.EnsureCurrentContextContract(node.Deserialize<AgentRunCheckpoint>(Json)); return true; } catch (InvalidOperationException) { return false; } }
    private static bool Rename(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("systemInstructions", out var instructions)) return false;
        if (obj.ContainsKey("agentInstructions")) throw new InvalidOperationException("Mixed instruction schema; migration refused.");
        obj.Remove("systemInstructions"); obj["agentInstructions"] = instructions; return true;
    }
    private static string Definition(string json, bool candidate = false)
    {
        var value = JsonNode.Parse(json)!.AsObject();
        if (value["schemaVersion"]?.GetValue<int>() == 1)
        {
            if (!Rename(value)) throw new InvalidOperationException("Old Definition is missing its instruction field; migration refused.");
            value["schemaVersion"] = 2;
        }
        EnsureDefinition(value.ToJsonString());
        if (candidate) AgentDefinitionValidator.Validate(value.Deserialize<AgentDefinitionCandidate>(Json)!.ToPublished(1));
        else AgentDefinitionValidator.Validate(value.Deserialize<AgentDefinition>(Json)!);
        return value.ToJsonString(Json);
    }
}
