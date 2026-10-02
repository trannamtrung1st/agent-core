using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Connections;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteApplicationConnectionStore(IDbContextFactory<AgentCoreDbContext> contexts)
    : IApplicationConnectionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<ApplicationConnection?> GetByAgentAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ApplicationConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgentInstanceId == agentInstanceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async ValueTask<ApplicationConnection> SaveAsync(
        ApplicationConnection connection,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = connection.AgentInstanceId.ToString("D");
        var row = await db.ApplicationConnections
            .SingleOrDefaultAsync(item => item.AgentInstanceId == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            if (expectedRevision != 0)
            {
                throw AgentCoreErrors.Conflict("Application connection revision is stale.");
            }

            db.ApplicationConnections.Add(ToRecord(connection));
        }
        else
        {
            if (row.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Application connection revision is stale.");
            }

            Copy(connection, row);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Conflict("Application connection revision is stale.");
        }

        return connection;
    }

    private static ApplicationConnectionRecord ToRecord(ApplicationConnection connection) =>
        new()
        {
            ConnectionId = connection.ConnectionId.ToString("D"),
            AgentInstanceId = connection.AgentInstanceId.ToString("D"),
            Kind = connection.Kind,
            DisplayName = connection.DisplayName,
            BaseUrl = connection.BaseUrl,
            TrustedOriginsJson = JsonSerializer.Serialize(connection.TrustedOrigins, Json),
            Status = connection.Status.ToString(),
            ProfileKey = connection.ProfileKey.ToString("D"),
            Revision = connection.Revision,
            CreatedAtUtc = connection.CreatedAtUtc.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = connection.UpdatedAtUtc.ToUnixTimeMilliseconds(),
            StatusDetail = connection.StatusDetail
        };

    private static void Copy(ApplicationConnection connection, ApplicationConnectionRecord row)
    {
        row.ConnectionId = connection.ConnectionId.ToString("D");
        row.Kind = connection.Kind;
        row.DisplayName = connection.DisplayName;
        row.BaseUrl = connection.BaseUrl;
        row.TrustedOriginsJson = JsonSerializer.Serialize(connection.TrustedOrigins, Json);
        row.Status = connection.Status.ToString();
        row.ProfileKey = connection.ProfileKey.ToString("D");
        row.Revision = connection.Revision;
        row.CreatedAtUtc = connection.CreatedAtUtc.ToUnixTimeMilliseconds();
        row.UpdatedAtUtc = connection.UpdatedAtUtc.ToUnixTimeMilliseconds();
        row.StatusDetail = connection.StatusDetail;
    }

    private static ApplicationConnection Map(ApplicationConnectionRecord row)
    {
        var origins = JsonSerializer.Deserialize<string[]>(row.TrustedOriginsJson, Json) ?? [];
        return new ApplicationConnection(
            Guid.Parse(row.ConnectionId),
            Guid.Parse(row.AgentInstanceId),
            row.Kind,
            row.DisplayName,
            row.BaseUrl,
            origins,
            Enum.Parse<ApplicationConnectionStatus>(row.Status),
            Guid.Parse(row.ProfileKey),
            row.Revision,
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc),
            row.StatusDetail);
    }
}
