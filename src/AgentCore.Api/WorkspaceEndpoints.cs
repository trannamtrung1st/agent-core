using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
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
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var nodes = await sessions.ListWorkspaceAsync(sessionId, prefix ?? "/", cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(nodes.Select(HttpMapping.ToWorkspaceNode).ToArray());
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("content", async (
            Guid sessionId,
            string? path,
            SessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw AgentCoreErrors.Validation("path is required.");
                }

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

                if (http.Request.ContentLength is > WorkspaceLimits.MaxWritableBytes)
                {
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
                        throw AgentCoreErrors.WorkspaceQuotaExceeded();
                    }

                    await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await sessions.WriteWorkspaceAsync(sessionId, path, buffer.ToArray(), cancellationToken)
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
