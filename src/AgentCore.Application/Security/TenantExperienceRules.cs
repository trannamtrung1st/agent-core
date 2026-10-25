using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantExperienceRules
{
    public const string Recorded = "experience_recorded", Consolidated = "experience_consolidated", ReadCompleted = "experience_source_read_completed";
    public const int MaxRetained = 500;
    public static string OwnerKey(TenantMemoryReader reader) => reader.User is { } user ? "user:" + user.PrincipalId.ToString("D") : "instance";
    public static bool Owns(TenantMemoryReader reader, TenantExperienceReference value) => value.TenantId == reader.Security.Tenant.TenantId
        && value.AgentInstanceId == reader.Run.AgentInstanceId && value.ExecutorPrincipalId == reader.Agent.PrincipalId
        && (value.UserPrincipalId is null && value.AccountId is null && value.UserId is null || value.UserPrincipalId == reader.User?.PrincipalId
            && value.AccountId == reader.User?.AccountId && value.UserId == reader.User?.UserId);
    public static void Eligible(TenantMemoryReader reader, TenantIsolationMetadata metadata, string tool)
    {
        if (reader.Session.Snapshot.RetentionKind == SessionRetentionKind.Temporary || reader.RootSession.Snapshot.RetentionKind == SessionRetentionKind.Temporary)
            throw new AccessDeniedException("temporary_session_memory_unavailable");
        if (!TenantTaskExecutionContract.Allows(reader.Run, reader.Current) || !metadata.ExperienceEnabled
            || !TenantExperienceToolContract.Allows(reader.Current, reader.Run.Admission.Configuration?.ContinuitySettings, tool)
            || !TenantExperienceToolContract.Allows(reader.Run.Admission.Configuration!.Definition, reader.Run.Admission.Configuration.ContinuitySettings, tool)
            || tool == TenantExperienceToolContract.Consolidate && (!metadata.AllowMaintenance || ManagementPolicyResolver.Resolve(reader.Current, reader.Agent.Instance).Frozen))
            throw new AccessDeniedException("experience_unavailable");
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantMemoryReader reader, TenantExperienceSourceWitness? source, bool facts, bool manage)
    {
        var tenant = reader.Security.Tenant.TenantId; var agent = AccessScope.ForResource(tenant, "agent", reader.Run.AgentInstanceId.ToString("D"));
        foreach (var id in new[] { reader.Run.SessionId, reader.Root.SessionId }.Distinct()) yield return ("sessions.read", TenantSessionRules.Scope(tenant, id));
        yield return ("agents.read", agent); yield return ("agents.chat", agent);
        if (source is not null)
        {
            yield return ("sessions.read", TenantSessionRules.Scope(tenant, source.SessionId));
            if (source.SourceRunId is { } run) yield return ("agent-runs.read", TenantRunRules.Scope(tenant, run));
        }
        if (facts) yield return ("memory.read", agent);
        if (manage) yield return ("memory.manage", agent);
    }
    public static async ValueTask RawAsync(TenantMemoryReader reader, TenantExperienceSourceWitness? source, bool facts, IamAuthorizer iam,
        CancellationToken ct, Guid? human = null, bool manage = false, bool exceptional = false, bool privateValue = false)
    {
        if (human is { } viewer && (!reader.Security.Users.Any(u => u.PrincipalId == viewer && u.Status == IdentityStatus.Active)
            || privateValue && reader.User is { } user && user.PrincipalId != viewer)) throw new AccessDeniedException("approval_unavailable");
        var actor = human ?? reader.Agent.PrincipalId;
        foreach (var (key, scope) in Targets(reader, source, facts, manage).Distinct())
        {
            if (human is null && exceptional && key == "memory.manage") continue;
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(human is null ? key.EndsWith(".read", StringComparison.Ordinal) ? "experience_read_required" : "experience_manage_required" : "approval_unavailable");
        }
    }
    public static async ValueTask<bool> OrdinaryAsync(TenantMemoryReader reader, TenantExperienceSourceWitness? source, bool facts, bool manage, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(reader, source, facts, manage).Distinct())
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) return false;
        return true;
    }
    public static void SourceShape(TenantExperienceSourceWitness value)
    {
        if (value.SourceId == Guid.Empty || value.SessionId == Guid.Empty || value.ThroughCursor < 1 || !TenantMemoryOperationRules.ValidHash(value.ObservationHash)
            || value.Kind == ExperienceSourceKind.Session && (value.SourceId != value.SessionId || value.SessionRevision < 1 || value.SourceRunId is not null
                || value.SourceRunRevision is not null || value.AfterEntrySequence < 0 || value.AfterEntrySequence >= value.ThroughCursor || value.Limit is < 1 or > 20 || value.PrivateUserPrincipalId is not null)
            || value.Kind == ExperienceSourceKind.AgentRun && (value.SourceRunId != value.SourceId || value.SourceRunRevision != value.ThroughCursor || value.PrivateUserPrincipalId == Guid.Empty
                || value.SessionRevision != 0 || value.AfterEntrySequence != 0 || value.Limit != 0)
            || value.Kind is not (ExperienceSourceKind.Session or ExperienceSourceKind.AgentRun)) throw new ArgumentException("Use an exact bounded Experience source witness.");
    }
    public static TenantExperienceReference[] Sources(PreparedExperienceOperation value)
    {
        var items = JsonSerializer.Deserialize<TenantExperienceReference[]>(value.SourcesJson) ?? throw new ArgumentException("Use exact Experience sources.");
        if (items.Length > 8 || items.Select(i => i.ExperienceId).Distinct().Count() != items.Length || JsonSerializer.Serialize(items) != value.SourcesJson
            || items.Any(i => i.TenantId == Guid.Empty || i.ExperienceId == Guid.Empty || i.AgentInstanceId == Guid.Empty || i.ExecutorPrincipalId == Guid.Empty || i.Revision < 1
                || !TenantMemoryOperationRules.ValidHash(i.ContentHash) || !Enum.IsDefined(i.Visibility))) throw new ArgumentException("Use canonical Experience source references."); return items;
    }
    public static Guid ResultId(TenantMemoryReader reader, TenantExperienceArguments args) => AgentCore.Application.Triggers.TriggerScheduleAdmission.OccurrenceId(
        "tenant-experience:" + reader.Security.Tenant.TenantId.ToString("D") + ":" + reader.Run.AgentInstanceId.ToString("D") + ":" + OwnerKey(reader) + ":" +
        (args.Tool == TenantExperienceToolContract.Record ? args.SourceKind + ":" + args.SourceId!.Value.ToString("D") + ":" + args.ThroughCursor : args.CanonicalArguments));
    public static string ContentHash(ExperienceContent content) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(content));
    public static TenantExperience Copy(TenantExperience value) => value with { Content = value.Content is null ? null : Freeze(value.Content) };
    public static ExperienceContent Freeze(ExperienceContent value)
    {
        value.Validate(); if (MemoryContentRules.ContainsSensitive(JsonSerializer.Serialize(value))) throw new AccessDeniedException("experience_sensitive_content");
        return value with { Attempts = value.Attempts.ToArray(), Decisions = value.Decisions.ToArray(), Outcomes = value.Outcomes.ToArray(), Corrections = value.Corrections.ToArray(),
            Unresolved = value.Unresolved.ToArray(), Difficulties = value.Difficulties.ToArray(), Lessons = value.Lessons.ToArray() };
    }
    // Fact references carry no owner. Stores resolve their actual rows under the same
    // source-validation transaction before classifying Instance-only evidence.
    public static bool HasPrivateEvidence(AgentRun run, bool privateExperienceFacts = true) => AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is not null
        || TenantMemoryOperationReferences.Read(run.Checkpoint).Count > 0
        || TenantExperienceReferences.Sources(run.Checkpoint).Any(i => i.Witness.PrivateUserPrincipalId is not null)
        || privateExperienceFacts && TenantExperienceReferences.Facts(run.Checkpoint).Count > 0;
    public static bool Substantive(AgentRun run) => run.IsTerminal && run.Checkpoint?.StepCount > 0 && run.Result?.OutcomeKind is not (AgentRunOutcomeKind.NoAction or AgentRunOutcomeKind.Continued)
        && (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages) || messages!.Any(m => m.Role == ModelRole.Tool
            && m.Name is { } tool && !TenantExperienceToolContract.IsTool(tool) && tool is not (ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet or ToolCatalog.WorkComplete or ToolCatalog.RequestAuthorization)));
    public static string SearchText(TenantExperience item) => item.Content is { } c ? c.Goal + " " + string.Join(' ', c.Attempts.Concat(c.Decisions).Concat(c.Outcomes).Concat(c.Corrections).Concat(c.Unresolved).Concat(c.Difficulties).Concat(c.Lessons)) : "";
}
