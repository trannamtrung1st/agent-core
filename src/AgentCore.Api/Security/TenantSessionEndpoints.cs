using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Security;

public static class TenantSessionEndpoints
{
    public sealed record Witness(long ExpectedRevision, long ExpectedSessionRevision);
    public sealed record RenameRequest(string Title, long ExpectedRevision, long ExpectedSessionRevision);
    public sealed record MessageRequest(Guid MessageId, string Text, long ExpectedRevision, long ExpectedSessionRevision, IReadOnlyList<Guid>? AttachmentIds = null, string? Behavior = null,
        Guid? InterruptedRunId = null, long? ExpectedRunRevision = null, AgentCore.Contracts.Realtime.UserMessagePartDto[]? Parts = null);
    public sealed record RunWitness(long ExpectedRevision, long ExpectedRunRevision);
    public sealed record CompletionPolicyRequest(bool Allowed, long ExpectedRevision, long ExpectedSessionRevision);
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/composer", (Guid instanceId, string category, string? search, string? cursor, Guid? spaceId, HttpContext http,
            TenantComposerReferenceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.DiscoverAsync(actor.TenantId, instanceId, Initiator(http, actor), category, search, cursor, ct, spaceId).ConfigureAwait(false));
        })).RequireAuthorization();
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions").RequireAuthorization();
        group.MapPut("/{sessionId:guid}/agent-completion", (Guid sessionId, CompletionPolicyRequest body, HttpContext http,
            TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var saved = await service.ChangeAsync(actor.TenantId, Initiator(http, actor),
                new(sessionId, body.ExpectedSessionRevision, TenantSessionAction.SetAgentCompletion, AgentCompletionAllowed: body.Allowed), body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(Safe(saved!));
        }));
        group.MapGet("/{sessionId:guid}/approvals", (Guid sessionId, int? page, int? pageSize, HttpContext http,
            TenantConversationApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAsync(actor.TenantId, sessionId, Initiator(http, actor), page ?? 0, pageSize ?? 20, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(item => new { item.RequestId, item.RunId, item.ResponseId,
                operation = item.Operation switch { AgentCore.Domain.Security.TenantOperationKind.ManagementMutation => "mutate_management", AgentCore.Domain.Security.TenantOperationKind.ManagementRead => "inspect_management", AgentCore.Domain.Security.TenantOperationKind.AutomationMutation => item.AutomationTool == AgentCore.Application.Tools.ToolCatalog.AutomationRun ? "invoke_automation" : "mutate_automation", AgentCore.Domain.Security.TenantOperationKind.AutomationRead => "inspect_automations", AgentCore.Domain.Security.TenantOperationKind.ContinuityRead => "inspect_continuity", AgentCore.Domain.Security.TenantOperationKind.MemoryOperation => item.MemoryRead == true ? "inspect_memory" : "manage_memory", AgentCore.Domain.Security.TenantOperationKind.Persona => "update_persona",
                    AgentCore.Domain.Security.TenantOperationKind.ExperienceOperation => AgentCore.Application.Tools.TenantExperienceToolContract.OperationName(item.ExperienceTool!),
                    AgentCore.Domain.Security.TenantOperationKind.ReportSourceRead => "inspect_background_report_source",
                    AgentCore.Domain.Security.TenantOperationKind.CompletionRead => "inspect_background_completion",
                    AgentCore.Domain.Security.TenantOperationKind.BackgroundReport => "deliver_background_completion",
                    AgentCore.Domain.Security.TenantOperationKind.BackgroundStart => "start_background_work",
                    AgentCore.Domain.Security.TenantOperationKind.WorkspaceRead => "inspect_workspace",
                    AgentCore.Domain.Security.TenantOperationKind.WorkspaceMutation => "mutate_workspace",
                    AgentCore.Domain.Security.TenantOperationKind.AttachmentRead => "read_attachment",
                    AgentCore.Domain.Security.TenantOperationKind.ArtifactVerification => "verify_artifact",
                    AgentCore.Domain.Security.TenantOperationKind.ArtifactPublication => "publish_artifact",
                    AgentCore.Domain.Security.TenantOperationKind.AccountAction => "use_connected_account",
            AgentCore.Domain.Security.TenantOperationKind.HttpRequest => "send_http_request",
                    AgentCore.Domain.Security.TenantOperationKind.AccountRead => "read_connected_account",
                    AgentCore.Domain.Security.TenantOperationKind.CredentialRead => "inspect_credential_metadata",
                    AgentCore.Domain.Security.TenantOperationKind.BrowserCommand => "browser_command",
                    AgentCore.Domain.Security.TenantOperationKind.SandboxRead => "sandbox_read",
                    AgentCore.Domain.Security.TenantOperationKind.SandboxControl => "sandbox_control",
                    AgentCore.Domain.Security.TenantOperationKind.ConversationPreferences => "update_preferences", AgentCore.Domain.Security.TenantOperationKind.SandboxCommand => "run_sandbox_command",
                    AgentCore.Domain.Security.TenantOperationKind.CredentialConnectionCheck => "check_credential_connection", _ => throw new InvalidOperationException("Unsupported prepared operation.") },
                kind = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(item.Kind.ToString()),
                status = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(item.Status.ToString()), item.CreatedAt, item.ExpiresAt,
                item.Waiting, item.CanInspect, outcome = item.Outcome is { } outcome ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()) : null }),
                result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{sessionId:guid}/conversation", (Guid sessionId, HttpContext http, TenantConversationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var view = await service.ReadViewAsync(actor.TenantId, sessionId, Initiator(http, actor), ct).ConfigureAwait(false);
            return Results.Json(new { view.Owner.TenantId, view.Owner.SessionId, view.Owner.AgentInstanceId, view.Title, view.Revision, view.SecurityRevision,
                mode = view.Mode.ToString().ToLowerInvariant(), pendingMode = (string?)null, status = view.Status.ToString().ToLowerInvariant(),
                lifecycleStatus = LifecycleTransition.ToWire(view.LifecycleStatus), view.CreatedAt, view.UpdatedAt,
                agentId = view.Agent.Id, agentVersion = view.Agent.Version, agentName = view.Agent.Name, agentRole = view.Agent.Role,
                model = HttpMapping.ToModel(view.Model, null), retentionKind = view.RetentionKind.ToString().ToLowerInvariant(),
                view.RetentionDueAtUtc, agentCompletion = view.AgentCompletion.ToString().ToLowerInvariant(), view.LifecycleReason,
                lifecycleSource = view.LifecycleSource?.ToString().ToLowerInvariant() });
        }));
        group.MapGet("/{sessionId:guid}/messages", (Guid sessionId, long? after, long? before, int? limit, HttpContext http,
            TenantConversationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var page = await service.ReadAsync(actor.TenantId, sessionId, Initiator(http, actor), after, before, limit ?? 50, ct).ConfigureAwait(false);
            return Results.Json(new { items = page.Items.Select(HttpMapping.ToHistoryItem), page.NextAfter, page.HasMore,
                page.HasOlder, page.NextBefore, page.SessionRevision, page.SecurityRevision });
        }));
        group.MapPost("/{sessionId:guid}/continue-in-chat", (Guid sessionId, Witness body, HttpContext http,
            TenantSessionForegroundService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.ContinueAsync(actor.TenantId, sessionId, Initiator(http, actor), body.ExpectedRevision, body.ExpectedSessionRevision, ct).ConfigureAwait(false);
            return Results.Json(new { sessionId });
        }));
        group.MapPost("/{sessionId:guid}/messages", (Guid sessionId, MessageRequest body, HttpContext http, TenantRunService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var interruption = TenantInputInterruptionRules.Request(body.Behavior, body.InterruptedRunId, body.ExpectedRunRevision);
            var receipt = await service.SubmitMessageAsync(actor.TenantId, sessionId, Initiator(http, actor), body.MessageId, body.Text, body.AttachmentIds ?? [],
                body.ExpectedRevision, body.ExpectedSessionRevision, ct, interruption, UserMessageMapping.FromWire(body.Parts)).ConfigureAwait(false);
            return Results.Json(receipt, statusCode: receipt.Created ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }));
        app.MapGet("/api/v2/tenants/{tenantId:guid}/agent-runs/{runId:guid}", (Guid runId, HttpContext http, TenantRunService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, runId, Initiator(http, actor), ct).ConfigureAwait(false);
            return Results.Json(Safe(result));
        })).RequireAuthorization();
        app.MapPost("/api/v2/tenants/{tenantId:guid}/agent-runs/{runId:guid}/cancel", (Guid runId, RunWitness body, HttpContext http, TenantRunService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.CancelAsync(actor.TenantId, runId, Initiator(http, actor), body.ExpectedRevision, body.ExpectedRunRevision, ct).ConfigureAwait(false)));
        })).RequireAuthorization();
        group.MapPatch("/{sessionId:guid}", (Guid sessionId, RenameRequest body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, body.ExpectedSessionRevision, TenantSessionAction.Rename, body.Title), body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(Safe(result!));
        }));
        foreach (var action in new[] { TenantSessionAction.Archive, TenantSessionAction.Restore, TenantSessionAction.Pause, TenantSessionAction.Resume, TenantSessionAction.Complete, TenantSessionAction.Cancel })
            group.MapPost("/{sessionId:guid}/" + action.ToString().ToLowerInvariant(), (Guid sessionId, Witness body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            {
                var actor = IdentityHttp.TenantContext(http);
                var result = await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, body.ExpectedSessionRevision, action), body.ExpectedRevision, ct).ConfigureAwait(false);
                return Results.Json(Safe(result!));
            }));
        group.MapDelete("/{sessionId:guid}", (Guid sessionId, long expectedRevision, long expectedSessionRevision, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, expectedSessionRevision, TenantSessionAction.Delete), expectedRevision, ct).ConfigureAwait(false); return Results.NoContent();
        }));
        group.MapPut("/{sessionId:guid}/model", (Guid sessionId, TenantSessionModelRequest body, HttpContext http,
            TenantSessionService service, AgentCore.Api.Realtime.TenantSessionRuntimeHost host, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var saved = await host.SelectModelAsync(service, actor.TenantId, sessionId, Initiator(http, actor), body.ExpectedRevision,
                body.ExpectedSessionRevision, body.OfferingId?.ToString("D"), body.ReasoningEffort, ct).ConfigureAwait(false);
            return Results.Json(new { sessionId, revision = saved.Snapshot.Revision, model = HttpMapping.ToModel(saved.Snapshot.ModelSelection, null) });
        }));
        group.MapPost("", (TenantSessionCreateRequest body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var initiator = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var item = await service.CreateAsync(actor.TenantId, body.AgentInstanceId, initiator, HttpMapping.ParseMode(body.Mode), body.ExpectedRevision, body.ExpectedInstanceRevision, body.OfferingId?.ToString("D"), body.ReasoningEffort, ct, body.RetentionKind switch { "saved" => AgentCore.Domain.Conversation.SessionRetentionKind.Saved,
                "temporary" => AgentCore.Domain.Conversation.SessionRetentionKind.Temporary, _ => throw AgentCoreErrors.Validation("retentionKind must be saved or temporary.") }).ConfigureAwait(false);
            return Results.Json(Safe(item), statusCode: 201);
        }));
        group.MapGet("", (int? page, int? pageSize, bool? includeArchived, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var result = await service.ListAsync(actor.TenantId, Initiator(http, actor), page ?? 0, pageSize ?? 30, ct, includeArchived ?? true).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{sessionId:guid}", (Guid sessionId, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.TenantId, sessionId, Initiator(http, actor), ct).ConfigureAwait(false)));
        }));
    }
    private static TenantSessionInitiator Initiator(HttpContext http, AuthenticatedTenant actor) =>
        new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(TenantRunMetadata result)
    {
        var c = result.Context;
        return new { context = new { c.TenantId, c.AgentPrincipalId, c.OriginalInitiatorPrincipalId, c.SessionId, c.RunId,
            c.ActivationId, c.RootActivationId, c.ParentRunId, c.ParentActivationId, origin = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(c.Origin.ToString()) },
            result.AgentInstanceId, result.SpaceContext, status = result.Status.ToString().ToLowerInvariant(), result.Revision, result.CreatedAt, result.UpdatedAt, result.ContinuationRunId, result.CanCancel,
            outcomeKind = result.OutcomeKind is { } outcome ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()) : null };
    }
    private static object Safe(TenantSessionMetadata item) => new { item.Owner.TenantId, item.Owner.SessionId, item.Owner.AgentInstanceId,
        item.Owner.ExecutorPrincipalId, item.Owner.CreatedByPrincipalId, item.Title, mode = item.Mode.ToString().ToLowerInvariant(),
        status = item.Status.ToString().ToLowerInvariant(), item.Revision, item.CreatedAt, item.UpdatedAt,
        lifecycleStatus = LifecycleTransition.ToWire(item.LifecycleStatus), item.ArchivedAt, item.AgentId, item.AgentVersion, item.AgentName, item.AgentRole, item.CanManage, item.PauseReason, item.ContextSelection,
        retentionKind = item.RetentionKind.ToString().ToLowerInvariant(), item.RetentionDueAtUtc,
        agentCompletion = item.AgentCompletion.ToString().ToLowerInvariant(), item.LifecycleReason, lifecycleSource = item.LifecycleSource?.ToString().ToLowerInvariant() };
}
