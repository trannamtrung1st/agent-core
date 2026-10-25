using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Composer;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantComposerChoice(string Id, string Label, string Description, string Category,
    string? SkillKey = null, UserResourceReference? Reference = null);
public sealed record TenantComposerPage(IReadOnlyList<TenantComposerChoice> Items, string? NextCursor);

// Typed selection is user input, never a delegated source-read grant. Native owners retain
// tenant ownership; current IAM, operation policy and source observations fence every body.
public sealed partial class TenantComposerReferenceService(IIamStore security, ITenantAgentStore agents,
    ITenantSessionStore sessions, ITenantRunStore runs, ITenantConversationStore conversations,
    ITenantWorkspaceReadStore files, ITenantArtifactStore artifacts, TenantRunConfigurationResolver configurations,
    IdentityService identity, IamAuthorizer iam, TenantOperationAuthorizer operations, TimeProvider time)
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxReferenceCharacters = 4000;
    public const int MaxAggregateCharacters = 12000;
    private static AccessScope AgentScope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
    private static AccessScope SkillScope(Guid tenant, Guid instance, string key) => TenantAgentChildScopes.Scope(tenant, instance, "skill", key);
    private static string Label(string value) => new(string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Take(200).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool TextType(string type) => type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type is "application/json" or "application/xml";
    private static string Body(byte[] bytes, string type)
    {
        if (bytes.Length > MaxBodyBytes || !TextType(type)) return "";
        try { return new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { return ""; }
    }
    private async ValueTask Permission(TenantSecurityState state, Guid actor, string key, AccessScope scope, CancellationToken ct)
    {
        if (!(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("composer_source_unavailable");
    }
    private async ValueTask<(TenantSecurityState State, TenantAgent Agent)> Human(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await Permission(state, human.PrincipalId, "agents.chat", AgentScope(tenant, instance), ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        return (state, agent);
    }
    private async ValueTask HumanFence(Guid tenant, Guid instance, TenantSessionInitiator human, TenantSecurityState state, TenantAgent agent, CancellationToken ct)
    {
        var after = await Human(tenant, instance, human, ct).ConfigureAwait(false);
        var authority = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (authority.Tenant.Revision != state.Tenant.Revision || after.State.Tenant.Revision != state.Tenant.Revision || after.Agent.Instance.Revision != agent.Instance.Revision)
            throw new SecurityConflictException("composer_source_changed");
    }
    private async ValueTask<IReadOnlyList<EffectiveSkill>> Skills(Guid tenant, Guid instance, CancellationToken ct)
    {
        var before = await configurations.CurrentPolicyAsync(tenant, instance, ct).ConfigureAwait(false);
        var saved = await agents.ReadTenantAgentConfigurationAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("composer_source_unavailable");
        if (before.Agent.Instance.Revision != saved.Agent.Instance.Revision) throw new SecurityConflictException("composer_source_changed");
        return AgentCore.Application.Agents.EffectiveSkillCatalogResolver.Resolve(before.Definition, saved.Skills);
    }
    public async ValueTask<IReadOnlyList<UserMessagePart>?> VisiblePartsAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        IReadOnlyList<UserMessagePart>? parts, CancellationToken ct)
    {
        if (parts is null) return null;
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var visible = new List<UserMessagePart>();
        foreach (var part in parts)
        {
            if (part.Kind == "text") { visible.Add(part); continue; }
            var reference = part.Reference ?? new UserResourceReference("skill", AgentInstanceId: instance, SkillKey: part.SkillKey);
            try
            {
                await Observe(tenant, instance, Guid.Empty, reference with { SelectedRevision = null }, false,
                    (key, scope) => Permission(state, human.PrincipalId, key, scope, ct), ct).ConfigureAwait(false);
                visible.Add(part);
            }
            catch (Exception error) when (error is AccessDeniedException or AgentCoreException) { visible.Add(new("text", "[Resource unavailable]")); }
        }
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("composer_source_changed");
        return visible.AsReadOnly();
    }
    public static IReadOnlyList<UserMessagePart>? Normalize(string text, IReadOnlyList<UserMessagePart>? parts)
    {
        try
        {
            var normalized = UserMessageContent.Normalize(parts, input: true);
            if (normalized is not null && text != UserMessageContent.DisplayText(normalized)) throw new ArgumentException("Text must match the canonical structured message.");
            return normalized;
        }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
    }
    public async ValueTask<IReadOnlyList<UserMessagePart>?> ValidateAsync(Guid tenant, Guid instance, Guid currentSession,
        TenantSessionInitiator human, IReadOnlyList<UserMessagePart>? parts, CancellationToken ct)
    {
        if (parts is null) return null;
        var (state, agent) = await Human(tenant, instance, human, ct).ConfigureAwait(false);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            var reference = part.Reference ?? (part.Kind == "invocation" ? new UserResourceReference("skill", AgentInstanceId: instance, SkillKey: part.SkillKey) : null);
            if (reference is null) continue;
            var value = await Observe(tenant, instance, currentSession, reference, false,
                (key, scope) => Permission(state, human.PrincipalId, key, scope, ct), ct).ConfigureAwait(false);
            labels[reference.Locator] = value.Label;
        }
        await HumanFence(tenant, instance, human, state, agent, ct).ConfigureAwait(false);
        return parts.Select(p => p.Kind == "reference" ? p with { Label = labels[p.Reference!.Locator] }
            : p.Kind == "invocation" ? p with { Label = labels[new UserResourceReference("skill", AgentInstanceId: instance, SkillKey: p.SkillKey).Locator] } : p).ToArray();
    }
    public async ValueTask<ComposerRunInput?> PinAsync(Guid instance, TrustedExecutionContext context, AgentDefinition pinned,
        IReadOnlyList<EffectiveSkill> catalog, IReadOnlyList<UserMessagePart>? parts, AgentCore.Domain.Spaces.RunSpaceWitness spaceContext, CancellationToken ct)
    {
        if (parts is null) return null;
        var tenant = context.TenantId;
        var current = await configurations.CurrentPolicyAsync(tenant, instance, ct).ConfigureAwait(false);
        if (current.Agent.PrincipalId != context.AgentPrincipalId) throw new AccessDeniedException("executor_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var keys = UserMessageContent.ExplicitSkills(parts);
        ComposerInputRules.ActiveSkills(pinned, catalog, keys);
        async ValueTask Allowed(string key, AccessScope scope)
        {
            await Permission(state, context.AgentPrincipalId, key, scope, ct).ConfigureAwait(false);
            var decision = await operations.Evaluate(state, context, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.Decision == OperationDecisionKind.RequiresApproval ? "composer_read_review_required" : "composer_source_unavailable");
        }
        foreach (var key in keys) await Allowed("skills.read", SkillScope(tenant, instance, key)).ConfigureAwait(false);
        var evidence = new List<ComposerReferenceEvidence>(); var remaining = MaxAggregateCharacters;
        foreach (var reference in parts.Where(p => p.Kind == "reference").Select(p => p.Reference!).DistinctBy(r => r.Locator))
        {
            ComposerReferenceEvidence value;
            try
            {
                Policy(pinned, current.Definition, reference, context.SessionId);
                value = await Observe(tenant, instance, context.SessionId, reference, true, Allowed, ct, spaceContext).ConfigureAwait(false);
            }
            catch (AccessDeniedException e) { value = new(reference, "Referenced " + reference.Kind, e.ReasonCode == "composer_read_review_required" ? "requiresApproval" : "forbidden", ""); }
            if (value.Text.Length > remaining) value = value with { Text = value.Text[..remaining], Truncated = true };
            remaining -= value.Text.Length; evidence.Add(value);
        }
        return new(keys, evidence.AsReadOnly(), time.GetUtcNow());
    }
    private static void Policy(AgentDefinition pinned, AgentDefinition current, UserResourceReference reference, Guid currentSession)
    {
        var isolation = AgentCore.Application.Agents.StateIsolationAccess.RestrictDefinition(pinned, current).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        if (reference.Kind == "homeFile" && isolation.HomeAccess == HomeAccess.Disabled
            || reference.SessionId is { } sourceSession && sourceSession != currentSession && (!isolation.CrossSessionContinuity || isolation.ConversationContextScope == ConversationContextScope.CurrentRequest))
            throw new AccessDeniedException("composer_source_unavailable");
    }
    public async ValueTask RequireAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Admission.ComposerInput is not { } input) return;
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("composer_source_unavailable");
        var saved = await runs.FindTenantRunAsync(tenant, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("composer_source_unavailable");
        if (saved.Owner != run.Owner || saved.Admission.ComposerInput is null) throw new AccessDeniedException("composer_source_unavailable");
        input = saved.Admission.ComposerInput;
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await TenantExecutionLineage.RequireRequesterAsync(saved, state, iam, runs, ct).ConfigureAwait(false);
        var current = await configurations.CurrentPolicyAsync(state.Tenant.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Agent.PrincipalId != saved.Owner.ExecutorPrincipalId || current.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, current.Agent);
        async ValueTask Allowed(string key, AccessScope scope)
        {
            await Permission(state, saved.Owner.ExecutorPrincipalId!.Value, key, scope, ct).ConfigureAwait(false);
            if ((await operations.Evaluate(state, saved.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false)).Decision != OperationDecisionKind.Allowed)
                throw new AccessDeniedException("composer_source_unavailable");
        }
        foreach (var key in input.ExplicitSkillKeys) await Allowed("skills.read", SkillScope(state.Tenant.TenantId, run.AgentInstanceId, key)).ConfigureAwait(false);
        foreach (var source in input.References.Where(r => r.Status is "valid" or "metadataOnly"))
        {
            Policy(run.Admission.Configuration!.Definition, current.Definition, source.Reference, run.SessionId);
            ComposerReferenceEvidence observed;
            try { observed = await Observe(state.Tenant.TenantId, run.AgentInstanceId, run.SessionId, source.Reference, true, Allowed, ct, TenantWorkspaceRules.Context(run)).ConfigureAwait(false); }
            catch (AgentCoreException) { throw new AccessDeniedException("composer_source_changed"); }
            if (observed.Sha256 != source.Sha256 || observed.Revision != source.Revision) throw new AccessDeniedException("composer_source_changed");
        }
        if ((await security.ReadTenantAsync(state.Tenant.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("composer_source_changed");
    }
    private async ValueTask<TenantWorkspaceReference.SpaceHome> HomeOwner(Guid tenant, Guid instance, Guid space, CancellationToken ct)
    {
        var store = (ITenantSpaceStore)security;
        if (await store.FindSpaceAsync(tenant, space, ct).ConfigureAwait(false) is null
            || !(await store.ReadMembershipsAsync(tenant, space, ct).ConfigureAwait(false)).Any(m => m.InstanceId == instance))
            throw new AccessDeniedException("composer_source_unavailable");
        return new(tenant, space);
    }
    private async ValueTask<ComposerReferenceEvidence> Observe(Guid tenant, Guid instance, Guid currentSession, UserResourceReference reference,
        bool content, Func<string, AccessScope, ValueTask> allowed, CancellationToken ct, AgentCore.Domain.Spaces.RunSpaceWitness? spaceContext = null)
    {
        reference.Validate();
        if (reference.AgentInstanceId is { } selected && selected != instance) throw new AccessDeniedException("composer_source_unavailable");
        await allowed("agents.read", AgentScope(tenant, instance)).ConfigureAwait(false);
        TenantSession? session = null;
        if (reference.SessionId is { } sessionId)
        {
            await allowed("sessions.read", TenantSessionRules.Scope(tenant, sessionId)).ConfigureAwait(false);
            session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("composer_source_unavailable");
            if (session.Owner.AgentInstanceId != instance || session.Snapshot.ArchivedAt is not null || session.Snapshot.DurablyDeletedAt is not null)
                throw new AccessDeniedException("composer_source_unavailable");
        }
        string label, text = "", witness; long? revision = null; var status = "valid"; var truncated = false;
        switch (reference.Kind)
        {
            case "homeFile":
                if (spaceContext is not null && reference.SpaceId != spaceContext.SpaceId && reference.SpaceId != spaceContext.PrivateSpaceId)
                    throw new AccessDeniedException("composer_source_unavailable");
                var owner = await HomeOwner(tenant, instance, reference.SpaceId!.Value, ct).ConfigureAwait(false);
                await allowed("spaces.read", TenantSpaceService.Scope(tenant, owner.SpaceId)).ConfigureAwait(false);
                await allowed("workspaces.read", TenantWorkspaceRules.Scope(owner)).ConfigureAwait(false);
                var path = TenantWorkspaceRules.Path(owner, reference.Path!, file: true);
                var file = await files.InspectTenantAsync(owner, path, 0, ct).ConfigureAwait(false);
                if (file.Item.Directory) throw AgentCoreErrors.Validation("Select a file.");
                label = file.Item.LogicalPath;
                if (content && file.Item.ByteSize <= MaxBodyBytes && TextType(file.Item.ContentType))
                {
                    var body = await files.InspectTenantAsync(owner, path, MaxBodyBytes, ct).ConfigureAwait(false);
                    if (body.Item != file.Item || body.Bytes.LongLength != file.Item.ByteSize) throw new SecurityConflictException("composer_source_changed");
                    text = Body(body.Bytes, file.Item.ContentType);
                    witness = Hash(Convert.ToBase64String(body.Bytes));
                }
                else witness = "";
                if ((await files.InspectTenantAsync(owner, path, 0, ct).ConfigureAwait(false)).Item != file.Item) throw new SecurityConflictException("composer_source_changed");
                witness = JsonSerializer.Serialize(new { file.Item, witness });
                status = file.Item.ByteSize > MaxBodyBytes || !TextType(file.Item.ContentType) || content && text.Length == 0 && file.Item.ByteSize > 0 ? "metadataOnly" : "valid";
                truncated = file.Item.ByteSize > MaxBodyBytes;
                break;
            case "session":
            case "backgroundSession":
                if (session!.Snapshot.Surfaces.HasFlag(SessionSurface.BackgroundWork) != (reference.Kind == "backgroundSession")) throw AgentCoreErrors.Validation("Reference category does not match its conversation.");
                label = session.Snapshot.Title; revision = session.Snapshot.Revision;
                if (content && session.Owner.SessionId == currentSession)
                {
                    if (reference.SelectedRevision is { } selectedRevision && selectedRevision != revision) throw AgentCoreErrors.Validation("The selected reference changed. Select its current version.");
                    return new(reference, Label(label), "alreadyInContext", "", revision);
                }
                if (content && reference.Kind == "backgroundSession" && session.Snapshot.Origin.InitialBackgroundAgentRunId is { } original)
                {
                    await allowed("agent-runs.read", TenantRunRules.Scope(tenant, original)).ConfigureAwait(false);
                    var initial = await runs.FindTenantRunAsync(tenant, original, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("composer_source_unavailable");
                    if (initial.SessionId != session.Owner.SessionId || initial.AgentInstanceId != instance) throw new AccessDeniedException("composer_source_unavailable");
                    text = $"Original task: {initial.Admission.TaskIntent?.Text ?? initial.Admission.Activation.EvidenceJson}\nStatus: {initial.Status}\nOriginal result: {initial.Result?.Text ?? initial.Failure?.Summary ?? "No completed result."}";
                    witness = JsonSerializer.Serialize(new { session.Owner, revision, label, runRevision = initial.Revision, text });
                }
                else
                {
                    var page = content ? await conversations.ReadTenantConversationAsync(tenant, session.Owner.SessionId, null, null, 20, ct).ConfigureAwait(false) : null;
                    if (page is not null && (page.Owner != session.Owner || page.SessionRevision != revision)) throw new SecurityConflictException("composer_source_changed");
                    if (page is not null) { text = string.Join('\n', page.History.Items.Where(e => e.IsPromptTurn).Select(e => { var item = PublicHistory.FromEntry(e); return $"{item.Role}: {item.Text}"; })); truncated = page.History.HasOlder; }
                    witness = JsonSerializer.Serialize(new { session.Owner, revision, label, text });
                }
                break;
            case "artifact":
                await allowed("artifacts.read", TenantArtifactRules.Scope(tenant, reference.ArtifactId!.Value)).ConfigureAwait(false);
                var artifact = await artifacts.FindTenantAsync(tenant, reference.SessionId!.Value, reference.ArtifactId.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("composer_source_unavailable");
                label = artifact.DisplayName;
                if (content && artifact.ByteSize <= MaxBodyBytes && TextType(artifact.ContentType))
                {
                    var bytes = await artifacts.ReadTenantContentAsync(tenant, artifact.SessionId, artifact.ArtifactId, ct).ConfigureAwait(false);
                    TenantArtifactRules.RequireContent(artifact, bytes); text = Body(bytes, artifact.ContentType);
                }
                status = artifact.ByteSize > MaxBodyBytes || !TextType(artifact.ContentType) || content && text.Length == 0 && artifact.ByteSize > 0 ? "metadataOnly" : "valid";
                truncated = artifact.ByteSize > MaxBodyBytes; witness = JsonSerializer.Serialize(artifact);
                if (await artifacts.FindTenantAsync(tenant, artifact.SessionId, artifact.ArtifactId, ct).ConfigureAwait(false) != artifact) throw new SecurityConflictException("composer_source_changed");
                break;
            case "agentRun":
                await allowed("agent-runs.read", TenantRunRules.Scope(tenant, reference.AgentRunId!.Value)).ConfigureAwait(false);
                var run = await runs.FindTenantRunAsync(tenant, reference.AgentRunId.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("composer_source_unavailable");
                if (run.SessionId != reference.SessionId || run.AgentInstanceId != instance || run.Owner.ExecutorPrincipalId != session!.Owner.ExecutorPrincipalId) throw new AccessDeniedException("composer_source_unavailable");
                label = $"{run.Admission.Activation.Kind} · {run.AgentRunId.ToString("D")[..8]}"; revision = run.Revision;
                if (content) text = $"Status: {run.Status}\nOutcome: {run.Result?.Text ?? run.Failure?.Summary ?? "No completed outcome."}";
                witness = JsonSerializer.Serialize(new { revision, label, text }); break;
            case "skill":
                await allowed("skills.read", SkillScope(tenant, instance, reference.SkillKey!)).ConfigureAwait(false);
                var catalog = await Skills(tenant, instance, ct).ConfigureAwait(false);
                var skill = catalog.SingleOrDefault(s => s.Key == reference.SkillKey) ?? throw new AccessDeniedException("composer_source_unavailable");
                label = skill.Name; if (content) text = $"Description: {skill.Description}\nProcedure (reference only, not activated): {skill.Procedure}";
                witness = JsonSerializer.Serialize(skill); break;
            default: throw AgentCoreErrors.Validation("Unknown reference kind.");
        }
        if (reference.SelectedRevision is { } expected && expected != revision) throw AgentCoreErrors.Validation("The selected reference changed. Remove it or select its current version.");
        if (session is not null && (await sessions.FindTenantSessionAsync(tenant, session.Owner.SessionId, ct).ConfigureAwait(false))?.Snapshot.Revision != session.Snapshot.Revision)
            throw new SecurityConflictException("composer_source_changed");
        if (text.Length > MaxReferenceCharacters) { text = text[..MaxReferenceCharacters]; truncated = true; }
        return new(reference, Label(label), status, text, revision, Hash(witness), truncated);
    }
}
