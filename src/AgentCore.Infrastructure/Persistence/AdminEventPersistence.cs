using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

internal static class AdminEventPersistence
{
    internal static void StageAppend(AgentCoreDbContext db, AdminEventAppend append, Guid eventId)
    {
        AdminEventSummaryPolicy.ValidateAppend(append);

        db.AdminEvents.Add(new AdminEventRecord
        {
            EventId = eventId.ToString("D"),
            OperationId = append.OperationId.ToString("D"),
            OccurredAtUtc = append.OccurredAt.ToUnixTimeMilliseconds(),
            ActorKind = append.ActorKind.ToString(),
            Operation = append.Operation.ToString(),
            TargetType = append.TargetType,
            TargetId = append.TargetId,
            Revision = append.Revision,
            Version = append.Version,
            SummaryJson = append.SummaryJson
        });
    }

    internal static AdminEvent Map(AdminEventRecord row) =>
        new(
            Guid.Parse(row.EventId),
            Guid.Parse(row.OperationId),
            DateTimeOffset.FromUnixTimeMilliseconds(row.OccurredAtUtc),
            Enum.Parse<AdminEventActorKind>(row.ActorKind),
            Enum.Parse<AdminEventOperationKind>(row.Operation),
            row.TargetType,
            row.TargetId,
            row.Revision,
            row.Version,
            row.SummaryJson);

    internal static async Task<AdminEvent?> TryGetByOperationIdAsync(
        AgentCoreDbContext db,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var row = await db.AdminEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == operationId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }
}
