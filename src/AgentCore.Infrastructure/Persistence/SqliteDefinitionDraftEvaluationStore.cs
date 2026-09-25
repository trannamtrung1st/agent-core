using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteDefinitionDraftEvaluationStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    IIdGenerator ids) : IDefinitionDraftEvaluationStore
{
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

    public async ValueTask<DefinitionEvaluationScenario> UpsertScenarioWithRevisionBumpAsync(
        Guid draftId,
        long expectedRevision,
        DefinitionEvaluationScenarioUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
