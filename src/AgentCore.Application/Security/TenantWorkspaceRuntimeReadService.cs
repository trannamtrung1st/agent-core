using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Office;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceRuntimeReadService(TenantOperationAuthorizer operations, ITenantRunStore runs,
    ITenantSessionStore sessions, ITenantWorkspaceReadStore files, TenantRunConfigurationResolver configurations,
    IIamStore security, IamAuthorizer iam, TimeProvider time, ITenantApprovalStore approvals, IIdGenerator ids, ITenantApprovedOperationStore effects, IOfficeProcessor? office = null, TenantResourceSourceTarget? resources = null, ITenantAttachmentStore? attachments = null)
{
    private sealed record Read(AgentRun Run, TenantSecurityState State, TenantSession Session, TenantAgent Agent, TenantWorkspaceReference Workspace, TenantResourceSourceObservation? Resource = null, ResourceCollection? Collection = null, PreparedKnowledgeReadSource? Knowledge = null, AttachmentCollection? Attachments = null);
    private async ValueTask<Read> Require(TenantRuntimeFrame frame, string tool, TenantWorkspaceReadArguments args, CancellationToken ct, bool audit = false, bool approved = false, bool live = true)
    {
        if (args.Path == "/attachments") return await RequireAttachments(frame, tool, args, ct, audit, approved, live).ConfigureAwait(false);
        if (tool == ToolCatalog.KnowledgeRetrieve)
        {
            var source = await (resources ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireKnowledgeMetadataAsync(frame, args.Identity!, ct, approved, live, audit).ConfigureAwait(false);
            var resolved = TenantKnowledgeReadContract.Resolve(source.Run.Admission.Configuration!.Definition, source.Run.Admission.Configuration.Resources, args.Identity!);
            return new(source.Run, source.Security, source.Session, source.Agent, new(source.Observation.TenantId, source.Observation.InstanceId), source.Observation, Knowledge: resolved.Source);
        }
        if (TenantWorkspaceReadContract.IsResourcePath(args.Path))
        {
            if (tool is ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch) return await RequireCollection(frame, tool, args, ct, audit, approved, live).ConfigureAwait(false);
            var source = await (resources ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireMetadataAsync(frame, tool, args.Path, ct, approved, live, audit).ConfigureAwait(false);
            return new(source.Run, source.Security, source.Session, source.Agent, new(source.Observation.TenantId, source.Observation.InstanceId), source.Observation);
        }
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.Owner != frame.Owner || run.SessionId != frame.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("resource_unavailable");
        if (!TenantTaskExecutionContract.Allows(run, run.Admission.Configuration!.Definition)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended) throw new AccessDeniedException("resource_unavailable");
        var current = await configurations.CurrentPolicyAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || !TenantTaskExecutionContract.Allows(run, current.Definition) || !TenantWorkspaceReadContract.Allows(run.Admission.Configuration!.Definition, tool)
            || !TenantWorkspaceReadContract.Allows(current.Definition, tool)) throw new AccessDeniedException("capability_unavailable");
        var owner = new TenantWorkspaceReference(tenant, run.AgentInstanceId, args.Path.StartsWith("/working", StringComparison.Ordinal) ? run.SessionId : null);
        TenantWorkspaceRules.Path(owner, args.Path, TenantWorkspaceReadContract.IsFile(tool));
        if (args.Cursor is not null && tool == ToolCatalog.WorkspaceList) TenantWorkspaceRules.Path(owner, args.Cursor);
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current.Definition).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        if (owner.SessionId is null && isolation.HomeAccess == HomeAccess.Disabled) throw new AccessDeniedException("home_access_disabled");
        var targets = new List<(string Permission, AccessScope Scope)> { ("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId)) };
        if (owner.SessionId is null) targets.Add(("agents.read", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D"))));
        targets.Add(("workspaces.read", TenantWorkspaceRules.Scope(owner)));
        foreach (var (key, scope) in targets)
        {
            if (!(await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            if (approved) continue;
            var decision = audit ? await operations.Check(state, run, key, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("workspace_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return new(run, state, session, current.Agent, owner);
    }
    private async ValueTask Fence(TenantRuntimeFrame frame, string tool, TenantWorkspaceReadArguments args, Read before, CancellationToken ct, bool approved = false, bool live = true)
    {
        ct.ThrowIfCancellationRequested();
        var after = await Require(frame, tool, args, ct, approved: approved, live: live).ConfigureAwait(false);
        if (before.Run.Revision != after.Run.Revision || before.State.Tenant.Revision != after.State.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Session.Owner != after.Session.Owner
            || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision || before.Workspace != after.Workspace || before.Resource != after.Resource || before.Collection?.Hash != after.Collection?.Hash || before.Knowledge != after.Knowledge || before.Attachments?.Hash != after.Attachments?.Hash)
            throw new SecurityConflictException("workspace_source_changed");
        if ((await security.ReadTenantAsync(after.Workspace.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != after.State.Tenant.Revision)
            throw new SecurityConflictException("workspace_source_changed");
        var final = await runs.FindTenantRunAsync(after.Workspace.TenantId, frame.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        if (live) TenantRuntimeRules.Require(frame, after.Session.Owner, final, time.GetUtcNow());
        if (final.Revision != after.Run.Revision) throw new SecurityConflictException("workspace_source_changed");
    }
    private static AgentWorkspaceContent ResourceContent(TenantResourceSource source, bool body)
    {
        var resource = source.Observation.Resource;
        return new(new(source.Observation.InstanceId, resource.VirtualPath, resource.MediaType, resource.ByteLength, DateTimeOffset.UnixEpoch), body ? source.CopyContent() : []);
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private async ValueTask<(Read Before, string Output, string Hash)> InspectCore(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes,
        CancellationToken ct, bool approved = false, bool live = true)
    {
        var args = TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson);
        var before = await Require(frame, call.Name, args, ct, !approved, approved, live).ConfigureAwait(false);
        string fingerprint = "";
        object Witness() => before.Collection is not null ? new { tool = call.Name, arguments = args, resourceCollection = true, observationHash = fingerprint } : before.Resource is null ? (object)new { tool = call.Name, arguments = args }
            : new { tool = call.Name, arguments = args, resource = before.Resource, observationHash = fingerprint };
        string result;
        if (before.Attachments is not null)
            (result, fingerprint) = await InspectAttachments(frame, call, args, before, remainingBytes, ct, approved, live).ConfigureAwait(false);
        else if (before.Collection is not null)
            (result, fingerprint) = await InspectCollection(frame, call, args, before, remainingBytes, ct, approved, live).ConfigureAwait(false);
        else if (call.Name == ToolCatalog.WorkspaceSearch)
            (result, fingerprint) = await Search(frame, args, before, remainingBytes, ct, approved, live).ConfigureAwait(false);
        else if (call.Name == ToolCatalog.WorkspaceList)
        {
            var page = await files.ListTenantAsync(before.Workspace, args.Path, args.Cursor, args.Limit, ct).ConfigureAwait(false);
            fingerprint = Hash(JsonSerializer.Serialize(new { page.Items, page.NextPath, page.Truncated }));
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
            var resourceSource = call.Name == ToolCatalog.KnowledgeRetrieve ? await (resources ?? throw new AccessDeniedException("tenant_effect_unavailable")).CaptureKnowledgeAsync(frame, args.Identity!, approved, live, ct).ConfigureAwait(false)
                : before.Resource is null ? null : await (resources ?? throw new AccessDeniedException("tenant_effect_unavailable"))
                .CaptureForInspectionAsync(frame, call.Name, args.Path, approved, live, ct).ConfigureAwait(false);
            if (resourceSource is not null && resourceSource.Observation != before.Resource) throw new SecurityConflictException("resource_source_changed");
            var metadata = resourceSource is null ? await files.InspectTenantAsync(before.Workspace, args.Path, 0, ct).ConfigureAwait(false)
                : ResourceContent(resourceSource, false);
            var officeRead = TenantWorkspaceReadContract.IsOffice(call.Name);
            if (officeRead && (office is null || metadata.Item.ByteSize > OfficeLimits.MaxPackageBytes))
                throw new AccessDeniedException(office is null ? "office_processing_unavailable" : "office_processing_limit");
            var content = resourceSource is null ? await files.InspectTenantAsync(before.Workspace, args.Path, officeRead ? OfficeLimits.MaxPackageBytes : checked((int)AgentWorkspaceLimits.MaxFileBytes), ct).ConfigureAwait(false) : ResourceContent(resourceSource, true);
            if (metadata.Item != content.Item || content.Bytes.LongLength != content.Item.ByteSize
                || resourceSource is null && (await files.InspectTenantAsync(before.Workspace, args.Path, 0, ct).ConfigureAwait(false)).Item != content.Item)
                throw new SecurityConflictException("workspace_source_changed");
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(content.Bytes));
            fingerprint = before.Resource is null ? Hash(JsonSerializer.Serialize(new { content.Item, hash = contentHash }))
                : before.Knowledge is null ? Hash(JsonSerializer.Serialize(new { content.Item, source = before.Resource, hash = contentHash }))
                : Hash(JsonSerializer.Serialize(new { content.Item, source = before.Resource, knowledge = before.Knowledge, hash = contentHash }));
            if (officeRead)
            {
                string Envelope(object page) => JsonSerializer.Serialize(new { path = args.Path, untrusted = true, inspection = page, tenantWorkspaceRead = true,
                    workspaceRead = JsonSerializer.SerializeToElement(Witness()) }, OfficeReadResults.Json);
                var budget = Math.Min(remainingBytes, 128 * 1024);
                result = call.Name == ToolCatalog.OfficeDocumentInspect
                    ? OfficeReadResults.Document(office!.InspectDocument(content.Bytes, args.Offset, args.Limit, ct), args.Offset, budget, Envelope)
                    : args.Range is null ? OfficeReadResults.SparseWorkbook(office!.ReadWorkbookContent(content.Bytes, args.Sheet, args.Cursor, ct), budget, Envelope)
                    : OfficeReadResults.Workbook(office!.InspectWorkbook(content.Bytes, args.Sheet, args.Range, ct), budget, Envelope);
            }
            else
            {
                try
                {
                    var text = new UTF8Encoding(false, true).GetString(content.Bytes);
                    if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\f'))) throw new DecoderFallbackException();
                    result = ToolJsonResults.FitJsonWithContentField(remainingBytes, ToolJsonResults.ClipUtf8Prefix(text, Math.Max(0, remainingBytes)),
                        (body, clipped) => call.Name == ToolCatalog.KnowledgeRetrieve
                            ? JsonSerializer.Serialize(new { identity = before.Knowledge!.Identity, title = before.Knowledge.Title, citation = before.Knowledge.Citation, content = body, truncated = clipped || body.Length < text.Length, untrusted = true, tenantWorkspaceRead = true, workspaceRead = Witness() })
                            : JsonSerializer.Serialize(new { path = args.Path, contentType = content.Item.ContentType, byteSize = content.Item.ByteSize,
                            truncated = clipped || body.Length < text.Length, content = body, untrusted = true, tenantWorkspaceRead = true, workspaceRead = Witness() }));
                }
                catch (DecoderFallbackException) { result = ToolJsonResults.FitToBudget(remainingBytes, """{"error":"unsupported_reader","message":"This file is not UTF-8 text. Use a supported reader."}"""); }
            }
        }
        await Fence(frame, call.Name, args, before, ct, approved, live).ConfigureAwait(false);
        return (before, result, fingerprint);
    }
    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default)
    {
        try
        {
            var observed = await InspectCore(frame, call, remainingBytes, ct).ConfigureAwait(false);
            var exact = TenantApprovalRules.PrepareWorkspaceRead(ids.NewId(), frame.Owner.AgentInstanceId,
                Witness(frame.SessionId, call, observed.Hash, observed.Before.Resource?.Resource.Key, observed.Before.Resource?.ResourceRevision, observed.Before.Collection?.MembersJson, observed.Before.Knowledge, observed.Before.Attachments?.MembersJson), observed.Before.Agent.Instance.Revision, observed.Before.Agent.Instance.PersonaRevision);
            var prior = await approvals.FindRootApprovalAsync(observed.Before.Workspace.TenantId, observed.Before.Run.Admission.ExecutionContext!.RootActivationId,
                observed.Before.Agent.PrincipalId, exact, ct).ConfigureAwait(false);
            if (prior is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(prior));
            return observed.Output;

        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant) await operations.RecordFailureAsync(new(tenant, frame.RunId), TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson).Path == "/attachments" ? "attachments.read" : call.Name == ToolCatalog.KnowledgeRetrieve || TenantWorkspaceReadContract.IsResourcePath(TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson).Path) ? "resources.read" : "workspaces.read",
                e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
        catch (Sessions.AgentCoreException e) when (TenantWorkspaceReadContract.IsOffice(call.Name))
        {
            var reason = new[] { "office_cursor_invalid", "office_revision_conflict", "office_processing_limit", "office_operation_unsupported", "invalid_office_package" }
                .FirstOrDefault(code => e.Code == code || e.Message.StartsWith(code + ":", StringComparison.Ordinal)) ?? "workspace_read_unavailable";
            return ToolJsonResults.FitToBudget(remainingBytes, JsonSerializer.Serialize(new { error = reason, message = "The exact Office observation is unavailable. Check its parameters and current source before requesting another read." }));
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
                args = TenantWorkspaceReadContract.Parse(message.Name!, TenantWorkspaceReadContract.Canonical(message.Name!, args));
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { throw new AccessDeniedException("workspace_source_changed"); }
            if (args.Path == "/attachments" || message.Name == ToolCatalog.KnowledgeRetrieve || TenantWorkspaceReadContract.IsResourcePath(args.Path))
            {
                using var doc = JsonDocument.Parse(message.Text); var witness = doc.RootElement.GetProperty("workspaceRead");
                var observed = await InspectCore(frame, new("resource-revalidate", message.Name!, TenantWorkspaceReadContract.Canonical(message.Name!, args)), ToolLimits.MaxOutputBytes, ct).ConfigureAwait(false);
                if (witness.GetProperty("observationHash").GetString() != observed.Hash
                    || observed.Before.Collection is null && observed.Before.Attachments is null && witness.GetProperty("resource").Deserialize<TenantResourceSourceObservation>() != observed.Before.Resource) throw new AccessDeniedException("resource_source_changed");
                continue;
            }
            var before = await Require(frame, message.Name!, args, ct).ConfigureAwait(false);
            await Fence(frame, message.Name!, args, before, ct).ConfigureAwait(false);
        }
        await RequireStoredApprovedAsync(run, messages!, ct).ConfigureAwait(false);
    }
}
