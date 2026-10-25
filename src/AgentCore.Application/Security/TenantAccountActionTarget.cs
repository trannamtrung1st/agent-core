using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAccountActionSource(AgentRun Run, TenantSecurityState State, TenantAgent Agent, TenantSession Session,
    AgentDefinition Baseline, TenantCredentialBinding Bound);
public sealed record TenantAccountPreparedAction(TenantAccountActionSource Source, PreparedAccountAction Action, TenantApprovalSnapshot Snapshot);

// Preparation performs no account/provider I/O. Remote metadata comes only from
// current exact read-consent observations already retained by this task.
public sealed class TenantAccountActionTarget(TenantOperationAuthorizer operations, ITenantRunStore runs, IIamStore security,
    ITenantSessionStore sessions, ITenantCredentialBindingStore bindings, TenantRunConfigurationResolver configurations, IamAuthorizer iam,
    TenantAccountRuntimeReadService reads, ITenantConnectedAccountTransportFactory transports, ITenantWorkspaceArtifactSourceStore files,
    ITenantWorkspaceMutationObservationStore destinations, IEmailStore drafts, IIdGenerator ids, TimeProvider time)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public async ValueTask<TenantAccountActionSource> SourceAsync(TenantRuntimeFrame frame, string alias, CancellationToken ct, bool live = true)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        else if (run.Status is not (AgentRunStatus.Running or AgentRunStatus.WaitingForApproval) || run.CancellationRequested) throw new AccessDeniedException("tenant_runtime_unavailable");
        if (frame.Owner != run.Owner || run.SessionId != frame.SessionId) throw new AccessDeniedException("tenant_context_mismatch");
        var (agent, baseline) = await configurations.BaselineAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        var snapshot = await configurations.CurrentCredentialBindingsAsync(run, ct, bindings).ConfigureAwait(false) ?? throw new AccessDeniedException("account_unavailable");
        var bound = snapshot.Bindings.SingleOrDefault(b => b.Binding.Reference == alias) ?? throw new AccessDeniedException("account_unavailable");
        if (snapshot.Agent.Instance.Revision != agent.Instance.Revision || snapshot.Agent.PrincipalId != run.Owner.ExecutorPrincipalId)
            throw new SecurityConflictException("account_source_changed");
        var after = await runs.FindTenantRunAsync(tenant, run.AgentRunId, ct).ConfigureAwait(false);
        if (after?.Revision != run.Revision || (await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("account_source_changed");
        return new(run, state, agent, session, baseline, bound);
    }
    public async ValueTask<TenantAccountPreparedAction> PrepareAsync(TenantRuntimeFrame frame, TenantAccountActionIntent intent, CancellationToken ct = default)
    {
        var args = TenantAccountActionContract.Parse(frame.SessionId, intent.ToolName, intent.ArgumentsJson);
        var before = await SourceAsync(frame, args.Reference, ct).ConfigureAwait(false);
        var action = new PreparedAccountAction(frame.SessionId, args.ToolName, args.Json, TenantCredentialOperationRules.Parameters(before.Bound), args.Purpose, args.Permission, "[]");
        // Validate independent raw permissions before capturing even private source metadata.
        Require(before, action); await Permissions(before, action, false, ct).ConfigureAwait(false);
        if (args.ToolName is ToolCatalog.EmailCreateDraft or ToolCatalog.EmailSend)
        {
            PreparedAccountDraft draft;
            if (args.DraftId is { } id) draft = await DraftAsync(before, id, ct).ConfigureAwait(false);
            else
            {
                using var json = JsonDocument.Parse(args.Json); var root = json.RootElement;
                draft = new("tenant-draft-" + ids.NewId().ToString("N"), JsonSerializer.Serialize(new { to = root.GetProperty("to"), cc = root.GetProperty("cc"), bcc = root.GetProperty("bcc") }), root.GetProperty("subject").GetString()!,
                    root.GetProperty("body").GetString()!, before.Bound.Credential.Value.EmailAccount!.EmailAddress, 1);
            }
            action = action with { Draft = draft };
        }
        else
        {
            await reads.RequireStoredAsync(before.Run, ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(args.Json); var input = json.RootElement;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (args.ToolName == ToolCatalog.DriveTrashBatch) foreach (var file in Drive.DriveMutationArguments.Manifest(input)) wanted.Add(file.FileId);
            else if (input.TryGetProperty("fileId", out var file)) wanted.Add(file.GetString()!);
            var parent = args.Upload ? input.GetProperty("folderId").GetString() : args.ToolName == ToolCatalog.DriveCreateFolder
                ? input.GetProperty("parentId").GetString() : args.ToolName == ToolCatalog.DriveMove ? input.GetProperty("destinationParentId").GetString() : null;
            if (parent is not (null or "root")) wanted.Add(parent);
            var observed = RemoteSources(before.Run, before.Bound).Where(f => wanted.Contains(f.Id)).GroupBy(f => f.Id, StringComparer.Ordinal).Select(g => g.Last()).OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
            action = action with { RemoteSourcesJson = JsonSerializer.Serialize(observed) };
            if (args.Upload)
            {
                var source = await files.CaptureArtifactSourceTenantAsync(TenantWorkspaceEffectRules.Owner(before.Run, args.Path!), args.Path!, ct).ConfigureAwait(false);
                if (source.Observation.Owner != TenantWorkspaceEffectRules.Owner(before.Run, args.Path!)) throw new SecurityConflictException("account_source_changed");
                action = action with { UploadSource = TenantArtifactSourceRules.Witness(source.Observation) };
            }
            if (args.Download) action = action with { DestinationObservationHash = await DestinationAsync(before.Run, args.Path!, ct).ConfigureAwait(false) };
        }
        _ = TenantApprovalRules.PrepareAccountAction(ids.NewId(), before.Run.AgentInstanceId, action, before.Agent.Instance.Revision, before.Agent.Instance.PersonaRevision);
        var after = await SourceAsync(frame, args.Reference, ct).ConfigureAwait(false); Same(before, after); Require(after, action);
        await Permissions(after, action, false, ct).ConfigureAwait(false);
        return new(after, action, TenantAccountReadRules.Snapshot(after.Agent, after.Bound));
    }
    public void Require(TenantAccountActionSource source, PreparedAccountAction action, bool exactSecret = true)
    {
        TenantAccountActionRules.Require(action, source.Run, source.Agent, source.Session, source.Baseline, source.Bound, exactSecret);
        if (!transports.Availability(source.Bound.Credential.Value, action.ToolName == ToolCatalog.EmailSend).Available) throw new AccessDeniedException("account_unavailable");
    }
    private ValueTask Permissions(TenantAccountActionSource source, PreparedAccountAction action, bool primary, CancellationToken ct) =>
        TenantAccountActionRules.PermissionsAsync(source.State, source.Agent.Instance.InstanceId, action, TenantWorkspaceRules.Context(source.Run), source.Agent.PrincipalId, iam, primary, ct);
    public async ValueTask<ITenantWorkspaceArtifactSource?> RequireSavedAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default, bool exactSecret = true)
    {
        var action = request.Operation.AccountAction ?? throw new SecurityConflictException("operation_mismatch");
        var current = await SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), action.Credential.Reference, ct, live: false).ConfigureAwait(false);
        Require(current, action, exactSecret);
        if (current.Run.Revision != run.Revision || current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || request.Snapshot?.Credential != TenantAccountReadRules.Snapshot(current.Agent, current.Bound).Credential)
            throw new SecurityConflictException("account_source_changed");
        await Permissions(current, action, request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
        var args = TenantAccountActionRules.Arguments(action);
        if (args.DraftId is { } draft && !SameDraft(await DraftAsync(current, draft, ct).ConfigureAwait(false), action.Draft!)) throw new SecurityConflictException("account_source_changed");
        ITenantWorkspaceArtifactSource? source = null;
        if (args.Upload)
        {
            source = await files.CaptureArtifactSourceTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, ct).ConfigureAwait(false);
            if (TenantArtifactSourceRules.Witness(source.Observation) != action.UploadSource) throw new SecurityConflictException("account_source_changed");
        }
        if (args.Download && await DestinationAsync(run, args.Path!, ct).ConfigureAwait(false) != action.DestinationObservationHash) throw new SecurityConflictException("account_source_changed");
        await reads.RequireStoredAsync(run, ct, live: false).ConfigureAwait(false);
        return source;
    }
    private async ValueTask<string> DestinationAsync(AgentRun run, string path, CancellationToken ct) =>
        (await destinations.ObserveMutationTenantAsync(TenantWorkspaceEffectRules.Owner(run, path), [new(path, false, false)], ct).ConfigureAwait(false)).Hash;
    private async ValueTask<PreparedAccountDraft> DraftAsync(TenantAccountActionSource source, string id, CancellationToken ct)
    {
        var stored = await drafts.GetDraftAsync(id, ct).ConfigureAwait(false); var d = stored?.Draft; var c = source.Bound.Credential.Value;
        if (stored?.AgentInstanceId != source.Run.AgentInstanceId || stored.SessionId != source.Run.SessionId || d is null || d.AccountId != c.CredentialId
            || d.AccountRef != source.Bound.Binding.Reference || d.BindingId != source.Bound.Binding.BindingId || d.BindingRevision != source.Bound.Binding.Revision
            || d.AccountRevision != c.Revision || d.AuthEpoch != TenantCredentialOAuthRules.Epoch(c) || d.Sender != c.EmailAccount?.EmailAddress)
            throw new AccessDeniedException("account_draft_unavailable");
        return new(d.DraftId, JsonSerializer.Serialize(new { to = d.To, cc = d.Cc, bcc = d.Bcc }), d.Subject, d.Body, d.Sender!, d.Revision);
    }
    private static bool SameDraft(PreparedAccountDraft left, PreparedAccountDraft right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static void Same(TenantAccountActionSource before, TenantAccountActionSource after)
    {
        if (before.Run.Revision != after.Run.Revision || before.State.Tenant.Revision != after.State.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision
            || TenantAccountReadReferences.Witness(before.Bound) != TenantAccountReadReferences.Witness(after.Bound)) throw new SecurityConflictException("account_source_changed");
    }
    private static IEnumerable<PreparedAccountRemoteFile> RemoteSources(AgentRun run, TenantCredentialBinding bound)
    {
        if (!Execution.AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) yield break;
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            if (!root.TryGetProperty("accountRead", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("tenantAccountRead", out var marker) || marker.ValueKind != JsonValueKind.True
                || data.GetProperty("accountReadTool").GetString() != ToolCatalog.DriveGet) continue;
            var witness = data.GetProperty("accountReadWitness").Deserialize<TenantAccountReadWitness>(WebJson);
            if (witness is null || !TenantAccountReadReferences.SameSource(witness, bound)) continue;
            var result = data.GetProperty("result").Deserialize<ConnectedAccountReadResult>(WebJson);
            if (result?.File is not { } file) continue;
            yield return new(file.Id, file.Name, file.MimeType, file.ByteSize, file.Version, file.Md5Checksum, file.Parents, file.Trashed);
        }
    }
}
