using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async Task<TenantExperienceFactObservation> ObserveExperienceFacts(AgentCoreDbContext db, AgentRun run, TenantContinuityArguments args, PreparedExperienceRead? expected, Guid? human, CancellationToken ct)
    {
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var settings = await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (human is null) TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
        else
        {
            await ValidateTenantActorAsync(db, reader.Security, human.Value, ct).ConfigureAwait(false);
            if (expected is null || TenantExperienceFactRules.Sources(expected).Any(i => i.UserPrincipalId is { } owner && owner != human)) throw new AccessDeniedException("approval_unavailable");
        }
        await TenantExperienceRules.RawAsync(reader, null, true, new IamAuthorizer(this), ct, human).ConfigureAwait(false);
        var keys = new[] { "instance", TenantExperienceRules.OwnerKey(reader) };
        var rows = db.Set<TenantExperienceRecord>().AsNoTracking().Where(i => i.TenantId == reader.Security.Tenant.TenantId && i.AgentInstanceId == run.AgentInstanceId.ToString("D") && keys.Contains(i.OwnerKey));
        if (expected is not null) { var ids = TenantExperienceFactRules.Sources(expected).Select(i => i.ExperienceId).ToArray(); rows = rows.Where(i => ids.Contains(i.ExperienceId)); }
        else rows = rows.Where(i => i.Visibility == (int)ExperienceVisibility.Eligible && (args.Id == null || i.ExperienceId == args.Id));
        var values = (await rows.OrderByDescending(i => i.UpdatedAtUtc).ThenBy(i => i.ExperienceId).Take(101).ToArrayAsync(ct).ConfigureAwait(false)).Select(i => i.Domain()).ToArray();
        return TenantExperienceFactRules.Observe(reader, settings, args, values.Take(100), expected, human is not null, values.Length > 100);
    }
    public async ValueTask<TenantExperienceFactObservation> ObserveTenantExperienceFactsAsync(TenantRunReference reference, Guid? generation, TenantContinuityArguments arguments,
        PreparedExperienceRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveExperienceFacts(db, run, TenantContinuityToolContract.Parse(arguments.Tool, arguments.CanonicalArguments), expected, inspectionHuman, ct).ConfigureAwait(false);
    }
}
