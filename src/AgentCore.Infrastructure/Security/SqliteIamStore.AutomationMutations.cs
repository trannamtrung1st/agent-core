using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore : ITenantAutomationMutationStore
{
    public async ValueTask<TenantAutomationMutationObservation> ObserveTenantAutomationMutationAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, PreparedAutomationMutation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("automation_context_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveAutomationMutation(db, run, tool, arguments, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantAutomationMutationObservation> ObserveAutomationMutation(AgentCoreDbContext db, AgentRun run, string tool, string arguments, PreparedAutomationMutation? expected, Guid? human, CancellationToken ct)
    {
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var args = TenantAutomationMutationContract.Parse(tool, arguments); TenantAutomationMutationRules.Eligible(reader, tool);
        if (human is { } actor) { if (expected is null) throw new AccessDeniedException("approval_unavailable"); await ValidateTenantActorAsync(db, reader.Security, actor, ct).ConfigureAwait(false); }
        var row = args.AutomationId is { } id ? await OwnedAutomations(db, reader.Security.Tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.AutomationId == id.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("automation_unavailable") : null;
        var current = row is null ? null : TriggerStoreMapping.ToRegistration(row);
        if (current is not null) { TenantAutomationRules.Owner(current, reader.Agent); if (current.Revision != args.ExpectedRevision) throw new SecurityConflictException("automation_source_changed"); }
        var ids = reader.Root.Admission.Activation.SourceEntryIds.Select(id => id.ToString("D")).ToArray();
        var entries = reader.Root.Admission.ExecutionContext!.Origin == ExecutionOrigin.UserMessage
            ? (await db.Entries.AsNoTracking().Where(e => e.SessionId == reader.Root.SessionId.ToString("D") && ids.Contains(e.EntryId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(SqliteMemoryStore.ToEntry).ToArray() : [];
        var intent = TenantAutomationMutationRules.Intent(reader, entries); var destinations = TenantAutomationMutationRules.Destinations(run, current, args.Json);
        var source = new TenantAutomationMutationObservation(reader, current,
            destinations.Target is { } target ? await ReadSandboxSession(db, reader.Security.Tenant.TenantId, target, ct).ConfigureAwait(false) : null,
            destinations.Report is { } report ? await ReadSandboxSession(db, reader.Security.Tenant.TenantId, report, ct).ConfigureAwait(false) : null, intent.Text, intent.Task);
        var iam = new IamAuthorizer(this);
        if (expected is not null) { TenantAutomationMutationRules.Validate(source, expected); await TenantAutomationMutationRules.RawAsync(source, expected, iam, ct, human).ConfigureAwait(false); }
        else await TenantAutomationMutationRules.BaseRawAsync(reader, current, iam, ct).ConfigureAwait(false);
        if (expected is null && !intent.Task)
        {
            var before = entries.Min(e => e.Sequence);
            var history = (await db.Entries.AsNoTracking().Where(e => e.SessionId == reader.Root.SessionId.ToString("D") && e.EntrySequence < before)
                .OrderByDescending(e => e.EntrySequence).Take(8).ToArrayAsync(ct).ConfigureAwait(false)).OrderBy(e => e.EntrySequence).Select(SqliteMemoryStore.ToEntry).ToArray();
            source = source with { ConversationEvidenceJson = TenantAutomationMutationRules.ConversationEvidence(reader, history, before) };
        }
        return source;
    }
}
