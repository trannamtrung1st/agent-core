using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantWorkspaceRuntimeReadService(TenantOperationAuthorizer operations, ITenantRunStore runs,
    ITenantSessionStore sessions, ITenantWorkspaceReadStore files, TenantRunConfigurationResolver configurations,
    IIamStore security, IamAuthorizer iam, TimeProvider time)
{
    private sealed record Read(AgentRun Run, TenantSecurityState State, TenantSession Session, TenantAgent Agent, TenantWorkspaceReference Workspace);
    private async ValueTask<Read> Require(TenantRuntimeFrame frame, string tool, TenantWorkspaceReadArguments args, CancellationToken ct, bool audit = false)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended) throw new AccessDeniedException("resource_unavailable");
        var current = await configurations.CurrentPolicyAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || !TenantWorkspaceReadContract.Allows(run.Admission.Configuration!.Definition, tool)
            || !TenantWorkspaceReadContract.Allows(current.Definition, tool)) throw new AccessDeniedException("capability_unavailable");
        var owner = new TenantWorkspaceReference(tenant, run.AgentInstanceId, args.Path.StartsWith("/working", StringComparison.Ordinal) ? run.SessionId : null);
        TenantWorkspaceRules.Path(owner, args.Path, tool == ToolCatalog.WorkspaceRead);
        if (args.Cursor is not null) TenantWorkspaceRules.Path(owner, args.Cursor);
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current.Definition).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        if (owner.SessionId is null && isolation.HomeAccess == HomeAccess.Disabled) throw new AccessDeniedException("home_access_disabled");
        var targets = new List<(string Permission, AccessScope Scope)> { ("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId)) };
        if (owner.SessionId is null) targets.Add(("agents.read", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D"))));
        targets.Add(("workspaces.read", TenantWorkspaceRules.Scope(owner)));
        foreach (var (key, scope) in targets)
        {
            if (!(await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            var decision = audit ? await operations.Check(state, run, key, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("workspace_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return new(run, state, session, current.Agent, owner);
    }
    private async ValueTask Fence(TenantRuntimeFrame frame, string tool, TenantWorkspaceReadArguments args, Read before, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var after = await Require(frame, tool, args, ct).ConfigureAwait(false);
        if (before.Run.Revision != after.Run.Revision || before.State.Tenant.Revision != after.State.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Session.Owner != after.Session.Owner
            || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision || before.Workspace != after.Workspace)
            throw new SecurityConflictException("workspace_source_changed");
        if ((await security.ReadTenantAsync(after.Workspace.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != after.State.Tenant.Revision)
            throw new SecurityConflictException("workspace_source_changed");
        var final = await runs.FindTenantRunAsync(after.Workspace.TenantId, frame.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantRuntimeRules.Require(frame, after.Session.Owner, final, time.GetUtcNow());
        if (final.Revision != after.Run.Revision) throw new SecurityConflictException("workspace_source_changed");
    }
    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        try
        {
            var args = TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson);
            var before = await Require(frame, call.Name, args, ct, true).ConfigureAwait(false);
            object Witness() => new { tool = call.Name, arguments = args };
            string result;
            if (call.Name == ToolCatalog.WorkspaceList)
            {
                var page = await files.ListTenantAsync(before.Workspace, args.Path, args.Cursor, args.Limit, ct).ConfigureAwait(false);
                var count = page.Items.Count;
                while (true)
                {
                    var clipped = count < page.Items.Count;
                    result = JsonSerializer.Serialize(new { path = args.Path, truncated = clipped || page.Truncated,
                        nextPath = clipped ? count > 0 ? page.Items[count - 1].LogicalPath : null : page.NextPath,
                        entries = page.Items.Take(count).Select(i => new { path = i.LogicalPath, directory = i.Directory, kind = i.Kind, byteSize = i.ByteSize,
                            modifiedAt = i.UpdatedAt, linkTarget = i.LinkTarget, mode = i.Mode }), tenantWorkspaceRead = true, workspaceRead = Witness() });
                    if (Encoding.UTF8.GetByteCount(result) <= remainingBytes && (count > 0 || page.Items.Count == 0)) break;
                    if (count == 0) { result = ToolJsonResults.FitToBudget(remainingBytes, """{"error":"output_limit"}"""); break; }
                    count--;
                }
            }
            else
            {
                var metadata = await files.InspectTenantAsync(before.Workspace, args.Path, 0, ct).ConfigureAwait(false);
                var content = await files.InspectTenantAsync(before.Workspace, args.Path, checked((int)AgentWorkspaceLimits.MaxFileBytes), ct).ConfigureAwait(false);
                if (metadata.Item != content.Item || content.Bytes.LongLength != content.Item.ByteSize
                    || (await files.InspectTenantAsync(before.Workspace, args.Path, 0, ct).ConfigureAwait(false)).Item != content.Item)
                    throw new SecurityConflictException("workspace_source_changed");
                try
                {
                    var text = new UTF8Encoding(false, true).GetString(content.Bytes);
                    if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\f'))) throw new DecoderFallbackException();
                    result = ToolJsonResults.FitJsonWithContentField(remainingBytes, ToolJsonResults.ClipUtf8Prefix(text, Math.Max(0, remainingBytes)),
                        (body, clipped) => JsonSerializer.Serialize(new { path = args.Path, contentType = content.Item.ContentType, byteSize = content.Item.ByteSize,
                            truncated = clipped || body.Length < text.Length, content = body, untrusted = true, tenantWorkspaceRead = true, workspaceRead = Witness() }));
                }
                catch (DecoderFallbackException) { result = ToolJsonResults.FitToBudget(remainingBytes, """{"error":"unsupported_reader","message":"This file is not UTF-8 text. Use a supported reader."}"""); }
            }
            await Fence(frame, call.Name, args, before, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant) await operations.RecordFailureAsync(new(tenant, frame.RunId), "workspaces.read",
                e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is Sessions.AgentCoreException or IOException or UnauthorizedAccessException)
        {
            return ToolJsonResults.FitToBudget(remainingBytes, """{"error":"workspace_read_unavailable","message":"The current path cannot be read. Check the exact path and current file state before another read."}""");
        }
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty);
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && TenantWorkspaceReadContract.IsRead(m.Name ?? "")))
        {
            TenantWorkspaceReadArguments args;
            try
            {
                using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
                if (!root.TryGetProperty("tenantWorkspaceRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
                var witness = root.GetProperty("workspaceRead");
                if (witness.GetProperty("tool").GetString() != message.Name) throw new AccessDeniedException("workspace_source_changed");
                args = witness.GetProperty("arguments").Deserialize<TenantWorkspaceReadArguments>() ?? throw new AccessDeniedException("workspace_source_changed");
                _ = TenantWorkspaceReadContract.Parse(message.Name!, JsonSerializer.Serialize(new { path = args.Path }));
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { throw new AccessDeniedException("workspace_source_changed"); }
            var before = await Require(frame, message.Name!, args, ct).ConfigureAwait(false);
            await Fence(frame, message.Name!, args, before, ct).ConfigureAwait(false);
        }
    }
}
