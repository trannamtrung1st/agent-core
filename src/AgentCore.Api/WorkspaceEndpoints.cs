using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public static class WorkspaceEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/workspace")
            .AddEndpointFilter<OwnerCapabilityFilter>()
            .DisableAntiforgery();

        group.MapGet("", async (
            Guid sessionId,
            string? prefix,
            string? cursor,
            int? limit,
            HttpContext http,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                AgentWorkspaceEndpoints.Query(http, "prefix", "cursor", "limit");
                var page = await sessions.ListWorkspacePageAsync(sessionId, prefix ?? "/home", cursor, limit ?? 256, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(new WorkspaceNodePageResponse(page.Entries.Select(HttpMapping.ToWorkspaceNode).ToArray(), page.NextPath, page.Truncated));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("content", async (
            Guid sessionId,
            string? path,
            HttpContext http,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw AgentCoreErrors.Validation("path is required.");
                }

                AgentWorkspaceEndpoints.Query(http, "path");
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                var content = await sessions.ReadWorkspaceAsync(sessionId, path, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Bytes(content.Bytes, content.ContentType);
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPut("content", async (
            Guid sessionId,
            string? path,
            HttpContext http,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw AgentCoreErrors.Validation("path is required.");
                }

                AgentWorkspaceEndpoints.Query(http, "path");
                if (http.Request.ContentLength is > WorkspaceLimits.MaxWritableBytes)
                {
                    OperationalDiagnostics.RecordResourceLimit("workspaceContentLength");
                    throw AgentCoreErrors.WorkspaceQuotaExceeded();
                }

                await using var buffer = new MemoryStream();
                var block = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await http.Request.Body.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > WorkspaceLimits.MaxWritableBytes)
                    {
                        OperationalDiagnostics.RecordResourceLimit("workspaceStream");
                        throw AgentCoreErrors.WorkspaceQuotaExceeded();
                    }

                    await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await sessions.WriteWorkspaceAsync(sessionId, path, buffer.ToArray(), cancellationToken,
                    http.Request.ContentType)
                    .ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }
}
