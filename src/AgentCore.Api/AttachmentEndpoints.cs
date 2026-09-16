using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api;

public static class AttachmentEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/attachments")
            .AddEndpointFilter<OwnerCapabilityFilter>()
            .DisableAntiforgery();

        group.MapGet("", async (
            Guid sessionId,
            SessionManager sessions,
            IAttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.EnsureAttachmentsAllowedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var records = await attachments.ListForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return Results.Json(records.Select(HttpMapping.ToAttachment).ToArray());
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("", async (
            Guid sessionId,
            HttpContext http,
            SessionManager sessions,
            IAttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.EnsureAttachmentsAllowedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var snapshot = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var allowUnread = RoleEnvironments.Of(snapshot.Definition).AttachmentPolicy.AllowUnreadUnsupportedTypes;
                if (!http.Request.HasFormContentType)
                {
                    throw AgentCoreErrors.Validation("Multipart form upload is required.");
                }

                var form = await http.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null)
                {
                    throw AgentCoreErrors.Validation("file is required.");
                }

                await using var stream = file.OpenReadStream();
                var record = await attachments.UploadPendingAsync(
                        sessionId,
                        file.FileName,
                        file.ContentType ?? "application/octet-stream",
                        stream,
                        allowUnread,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(HttpMapping.ToAttachment(record), statusCode: StatusCodes.Status201Created);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(AttachmentLimits.MaxBytesEach + (1024 * 1024)));

        group.MapGet("{attachmentId:guid}", async (
            Guid sessionId,
            Guid attachmentId,
            SessionManager sessions,
            IAttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.EnsureAttachmentsAllowedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var record = await attachments.GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false)
                    ?? throw AgentCoreErrors.NotFound("Session was not found.");
                return Results.Json(HttpMapping.ToAttachment(record));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("{attachmentId:guid}/content", async (
            Guid sessionId,
            Guid attachmentId,
            SessionManager sessions,
            IAttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.EnsureAttachmentsAllowedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var record = await attachments.GetAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false)
                    ?? throw AgentCoreErrors.NotFound("Session was not found.");
                var stream = await attachments.OpenContentAsync(sessionId, attachmentId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.File(stream, record.ContentType, fileDownloadName: record.DisplayName, enableRangeProcessing: false);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("{attachmentId:guid}/materialize", async (
            Guid sessionId,
            Guid attachmentId,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var artifact = await sessions.MaterializeAttachmentAsync(sessionId, attachmentId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(HttpMapping.ToArtifact(artifact), statusCode: StatusCodes.Status201Created);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapDelete("{attachmentId:guid}", async (
            Guid sessionId,
            Guid attachmentId,
            SessionManager sessions,
            IAttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.EnsureAttachmentsAllowedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                await attachments.AbortPendingAsync(sessionId, attachmentId, cancellationToken).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("stage", async (
            Guid sessionId,
            StageAttachmentsRequest? body,
            SessionManager sessions,
            SessionHost host,
            IAttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await sessions.EnsureAttachmentsAllowedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var ids = ParseIds(body?.AttachmentIds);
                await attachments.StageForNextTurnAsync(sessionId, ids, cancellationToken).ConfigureAwait(false);
                if (host.HasLiveRuntime(sessionId))
                {
                    await host.StageAttachmentsAsync(sessionId, ids, cancellationToken).ConfigureAwait(false);
                }

                return Results.NoContent();
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }

    private static IReadOnlyList<Guid> ParseIds(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            throw AgentCoreErrors.Validation("attachmentIds is required.");
        }

        var ids = new List<Guid>(raw.Count);
        foreach (var value in raw)
        {
            if (!Guid.TryParse(value, out var id))
            {
                throw AgentCoreErrors.Validation("attachmentIds must be UUIDs.");
            }

            ids.Add(id);
        }

        return ids;
    }
}
