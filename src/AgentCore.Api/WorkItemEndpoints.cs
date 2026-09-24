using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Work;

namespace AgentCore.Api;

public static class WorkItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/work-items")
            .AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("", async (
            Guid sessionId,
            int? limit,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IWorkItemStore work,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (limit is < 1)
                {
                    throw AgentCoreErrors.Validation("limit must be at least 1.");
                }

                var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
                var rows = await work.ListAsync(owner, limit ?? 50, cancellationToken).ConfigureAwait(false);
                return Results.Json(new WorkItemListResponse(rows.Select(ToResponse).ToArray()));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/{workItemId:guid}", async (
            Guid sessionId,
            Guid workItemId,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IWorkItemStore work,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var item = await RequireItemAsync(sessions, profiles, work, sessionId, workItemId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(ToResponse(item));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapGet("/{workItemId:guid}/result", async (
            Guid sessionId,
            Guid workItemId,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IWorkItemStore work,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var item = await RequireItemAsync(sessions, profiles, work, sessionId, workItemId, cancellationToken)
                    .ConfigureAwait(false);
                if (item.Result is not WorkResult result)
                {
                    throw AgentCoreErrors.NotFound("Work result was not found.");
                }

                return Results.Json(new WorkItemResultResponse(
                    item.WorkItemId.ToString(),
                    result.Text,
                    HttpMapping.Format(result.CompletedAtUtc)));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/{workItemId:guid}/cancel", async (
            Guid sessionId,
            Guid workItemId,
            CancelWorkItemRequest? body,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            DurableReminderExecutor executor,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (body is null || body.ExpectedRevision < 1)
                {
                    throw AgentCoreErrors.Validation("expectedRevision is required.");
                }

                var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
                var cancelled = await executor.RequestCancellationAsync(
                    owner,
                    workItemId,
                    body.ExpectedRevision,
                    knownEffectSummary: null,
                    time.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(ToResponse(cancelled));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/{workItemId:guid}/approvals/{approvalId:guid}/approve", (
            Guid sessionId,
            Guid workItemId,
            Guid approvalId,
            DecideWorkApprovalRequest? body,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IWorkItemStore work,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            DecideAsync(
                sessionId,
                workItemId,
                approvalId,
                body,
                WorkApprovalDecision.Approved,
                sessions,
                profiles,
                work,
                time,
                cancellationToken));

        group.MapPost("/{workItemId:guid}/approvals/{approvalId:guid}/reject", (
            Guid sessionId,
            Guid workItemId,
            Guid approvalId,
            DecideWorkApprovalRequest? body,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IWorkItemStore work,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            DecideAsync(
                sessionId,
                workItemId,
                approvalId,
                body,
                WorkApprovalDecision.Rejected,
                sessions,
                profiles,
                work,
                time,
                cancellationToken));
    }

    private static async Task<IResult> DecideAsync(
        Guid sessionId,
        Guid workItemId,
        Guid approvalId,
        DecideWorkApprovalRequest? body,
        WorkApprovalDecision decision,
        SessionManager sessions,
        ILocalUserProfileService profiles,
        IWorkItemStore work,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        try
        {
            if (body is null || body.ExpectedRevision < 1 || body.ExpectedApprovalRevision < 1 || string.IsNullOrWhiteSpace(body.ActionHash))
            {
                throw AgentCoreErrors.Validation("expectedRevision, expectedApprovalRevision, and actionHash are required.");
            }

            var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
            var updated = await work.DecideApprovalAsync(
                owner,
                workItemId,
                approvalId,
                body.ExpectedRevision,
                body.ExpectedApprovalRevision,
                body.ActionHash,
                decision,
                time.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Results.Json(ToResponse(updated));
        }
        catch (AgentCoreException ex)
        {
            return ProblemResults.From(ex);
        }
    }

    private static async Task<WorkOwner> RequireOwnerAsync(
        SessionManager sessions,
        ILocalUserProfileService profiles,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var local = await profiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.ProfileId != local.ProfileId || snapshot.AgentInstanceId is not Guid instanceId)
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return new WorkOwner(instanceId, local.ProfileId);
    }

    private static async Task<WorkItem> RequireItemAsync(
        SessionManager sessions,
        ILocalUserProfileService profiles,
        IWorkItemStore work,
        Guid sessionId,
        Guid workItemId,
        CancellationToken cancellationToken)
    {
        var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
        return await work.GetAsync(owner, workItemId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Work item was not found.");
    }

    private static WorkItemResponse ToResponse(WorkItem item)
    {
        var summary = item.ToPublicSummary();
        var approval = item.Status == WorkItemStatus.WaitingForApproval ? item.Approval : null;
        return new WorkItemResponse(
            summary.WorkItemId.ToString(),
            ToStatus(summary.Status),
            summary.Revision,
            summary.OriginLabel,
            summary.ProgressSummary,
            summary.NeedsApproval,
            approval?.ApprovalId.ToString(),
            approval?.Revision,
            summary.ApprovalPreview,
            approval?.ActionHash,
            summary.CancellationAvailable,
            summary.FailureCode,
            summary.FailureSummary,
            summary.KnownEffectSummary,
            HttpMapping.Format(summary.CreatedAtUtc),
            HttpMapping.Format(summary.UpdatedAtUtc));
    }

    private static string ToStatus(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Queued => "queued",
        WorkItemStatus.Running => "running",
        WorkItemStatus.WaitingForApproval => "needsApproval",
        WorkItemStatus.WaitingToRetry => "retrying",
        WorkItemStatus.Completed => "completed",
        WorkItemStatus.Failed => "failed",
        WorkItemStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
