using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteDefinitionDraftEvaluationStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    IIdGenerator ids,
    int busyTimeoutMs = 5000) : IDefinitionDraftEvaluationStore
{
    private readonly SemaphoreSlim _revisionWrites = new(1, 1);
    private readonly TimeSpan _writeAdmissionTimeout = TimeSpan.FromMilliseconds(Math.Max(1, busyTimeoutMs));

    public async ValueTask<IReadOnlyList<DefinitionEvaluationScenario>> ListScenariosAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentDefinitionDraftEvaluationScenarios.AsNoTracking()
            .Where(row => row.DraftId == draftId.ToString("D"))
            .OrderBy(row => row.ScenarioId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(DefinitionDraftEvaluationPersistence.MapScenario).ToArray();
    }

    public ValueTask<DefinitionEvaluationScenario> UpsertScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        DefinitionEvaluationScenarioUpsert upsert,
        CancellationToken cancellationToken = default) =>
        ExecuteRevisionWriteAsync(
            () => UpsertScenarioCoreAsync(draftId, expectedRevision, upsert, cancellationToken),
            cancellationToken);

    private async Task<DefinitionEvaluationScenario> UpsertScenarioCoreAsync(
        Guid draftId,
        long expectedRevision,
        DefinitionEvaluationScenarioUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        BoundWriteTimeout(db);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var draftKey = draftId.ToString("D");
        var draftRow = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == draftKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
        if (draftRow.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        draftRow.Revision += 1;
        draftRow.UpdatedAtUtc = upsert.UpdatedAt.ToUnixTimeMilliseconds();

        var scenarioRow = await db.AgentDefinitionDraftEvaluationScenarios
            .SingleOrDefaultAsync(
                item => item.DraftId == draftKey && item.ScenarioId == upsert.ScenarioId,
                cancellationToken)
            .ConfigureAwait(false);
        var nextVersion = scenarioRow is null ? 1 : scenarioRow.ScenarioVersion + 1;
        var scenario = new DefinitionEvaluationScenario(
            upsert.DraftId,
            upsert.ScenarioId,
            nextVersion,
            upsert.Title,
            upsert.Prompt,
            upsert.RequirementLevel,
            upsert.CheckType,
            upsert.ToolName,
            upsert.UpdatedAt);
        var mapped = DefinitionDraftEvaluationPersistence.MapScenario(scenario);
        if (scenarioRow is null)
        {
            db.AgentDefinitionDraftEvaluationScenarios.Add(mapped);
        }
        else
        {
            scenarioRow.ScenarioVersion = mapped.ScenarioVersion;
            scenarioRow.Title = mapped.Title;
            scenarioRow.Prompt = mapped.Prompt;
            scenarioRow.RequirementLevel = mapped.RequirementLevel;
            scenarioRow.CheckType = mapped.CheckType;
            scenarioRow.ToolName = mapped.ToolName;
            scenarioRow.UpdatedAtUtc = mapped.UpdatedAtUtc;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        return scenario;
    }

    public async ValueTask RemoveScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        string scenarioId,
        CancellationToken cancellationToken = default) =>
        await ExecuteRevisionWriteAsync(async () =>
        {
            await RemoveScenarioCoreAsync(draftId, expectedRevision, scenarioId, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private async Task RemoveScenarioCoreAsync(
        Guid draftId,
        long expectedRevision,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        BoundWriteTimeout(db);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var draftKey = draftId.ToString("D");
        var scenarioRow = await db.AgentDefinitionDraftEvaluationScenarios
            .SingleOrDefaultAsync(
                item => item.DraftId == draftKey && item.ScenarioId == scenarioId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Evaluation scenario was not found.");

        var draftRow = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == draftKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
        if (draftRow.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        draftRow.Revision += 1;
        draftRow.UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.AgentDefinitionDraftEvaluationScenarios.Remove(scenarioRow);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }
    }

    private void BoundWriteTimeout(AgentCoreDbContext db)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var seconds = (int)Math.Ceiling(_writeAdmissionTimeout.TotalSeconds);
        connection.DefaultTimeout = connection.DefaultTimeout == 0 ? seconds : Math.Min(connection.DefaultTimeout, seconds);
        db.Database.SetCommandTimeout(connection.DefaultTimeout);
    }

    private async ValueTask<T> ExecuteRevisionWriteAsync<T>(Func<Task<T>> write, CancellationToken cancellationToken)
    {
        // SQLite has one writer. Admit revision/scenario transactions before opening
        // a context so competing requests cannot overlap transaction startup here.
        if (!await _revisionWrites.WaitAsync(_writeAdmissionTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw AgentCoreErrors.Conflict("Draft evaluation write is busy. Reload the draft before retrying.");
        }

        try
        {
            return await write().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw AgentCoreErrors.Conflict("Draft evaluation write is busy. Reload the draft before retrying.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 5 or 6 })
        {
            throw AgentCoreErrors.Conflict("Draft evaluation write is busy. Reload the draft before retrying.");
        }
        finally
        {
            _revisionWrites.Release();
        }
    }

    public async ValueTask<IReadOnlyList<DefinitionEvaluationResult>> ListResultsAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentDefinitionDraftEvaluationResults.AsNoTracking()
            .Where(row => row.DraftId == draftId.ToString("D"))
            .OrderByDescending(row => row.RecordedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(DefinitionDraftEvaluationPersistence.MapResult).ToArray();
    }

    public async ValueTask<DefinitionEvaluationResult?> GetLatestResultAsync(
        Guid draftId,
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDraftEvaluationResults.AsNoTracking()
            .Where(item => item.DraftId == draftId.ToString("D") && item.ScenarioId == scenarioId)
            .OrderByDescending(item => item.RecordedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : DefinitionDraftEvaluationPersistence.MapResult(row);
    }

    public async ValueTask<DefinitionEvaluationResult> SaveResultAsync(
        DefinitionEvaluationResult result,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var mapped = DefinitionDraftEvaluationPersistence.MapResult(result, ids.NewId());
        db.AgentDefinitionDraftEvaluationResults.Add(mapped);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
