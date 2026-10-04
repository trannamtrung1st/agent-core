using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api;

internal static class HarnessManagementEndpoints
{
    internal static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}/harness");
        group.MapGet("", (Guid instanceId, HarnessManagementService service, CancellationToken ct) =>
            Respond(() => service.ReviewAsync(instanceId, ct)));
        group.MapPut("/policy", (Guid instanceId, HarnessPolicyRequest request, HarnessManagementService service, CancellationToken ct) =>
            Respond(async () =>
            {
                var mode = Parse<HarnessManagementMode>(request.Mode);
                var policy = new HarnessManagementPolicy(mode, request.Scopes.Select(Parse<HarnessManagementScope>).ToArray(),
                    request.Sources, request.EligibleTools, request.Frozen);
                await service.ConfigureAsync(instanceId, request.ExpectedRevision, policy, ct);
                return await service.ReviewAsync(instanceId, ct);
            }));
        group.MapPost("/prepare", (Guid instanceId, HarnessStartRequest request, HarnessManagementService service,
            CancellationToken ct) => Respond(async () =>
            {
                var instance = await service.StartAsync(instanceId, request.ExpectedRevision, request.Purpose, ct);
                return await service.ReviewAsync(instanceId, ct);
            }));
        group.MapPost("/verify", (Guid instanceId, HarnessRunRequest request, HarnessManagementService service, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.VerifyAsync(instanceId, ParseGuid(request.PreparationId), ct);
                return await service.ReviewAsync(instanceId, ct);
            }));
        group.MapPost("/cancel", (Guid instanceId, HarnessRevisionRequest request, HarnessManagementService service, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.CancelAsync(instanceId, request.ExpectedRevision, ct);
                return await service.ReviewAsync(instanceId, ct);
            }));
        group.MapPost("/approvals/{approvalId:guid}", (Guid instanceId, Guid approvalId, HarnessApprovalRequest request,
            HarnessManagementService service, CancellationToken ct) => Respond(async () =>
            {
                await service.DecideApprovalAsync(instanceId, request.ExpectedRevision, approvalId, request.ActionHash, request.Approve, ct);
                return await service.ReviewAsync(instanceId, ct);
            }));
        group.MapPost("/publish-adopt", (Guid instanceId, HarnessPromotionRequest request, HarnessManagementService service, CancellationToken ct) =>
            Respond(async () =>
            {
                await service.PromoteAsync(instanceId, request.ExpectedRevision, request.DraftRevision, ct);
                return await service.ReviewAsync(instanceId, ct);
            }));
    }

    private static async Task<IResult> Respond(Func<ValueTask<HarnessReview>> action)
    {
        try { return Results.Json(ToResponse(await action())); }
        catch (AgentCoreException exception) { return ProblemResults.From(exception); }
        catch (ArgumentException) { return ProblemResults.From(AgentCoreErrors.Validation("Harness request is invalid.")); }
    }

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<T>(value)
            : throw AgentCoreErrors.Validation("Unknown harness policy value.");
    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id
        : throw AgentCoreErrors.Validation("Preparation identity is invalid.");
    private static HarnessSkillResponse Skill(SkillSpec value) =>
        new(value.Id, value.Name, value.Description, value.Procedure, value.RequiredCapabilities, value.ResourcePaths);
    private static HarnessOperationResponse Operation(HarnessAuthoringOperation value) =>
        new(value.Kind, value.DraftRevision, value.Id, value.Content, value.Source,
            value.Skill is null ? null : Skill(value.Skill), value.Enabled, value.AllowUnreadUnsupportedTypes);
    private static HarnessReviewResponse ToResponse(HarnessReview review)
    {
        var state = review.State;
        var p = state.Preparation;
        return new(review.InstanceId.ToString("D"), review.InstanceRevision, review.ActiveVersion,
            new(state.Policy.Mode.ToString(), state.Policy.Scopes.Select(s => s.ToString()).ToArray(), state.Policy.Sources, state.Policy.EligibleTools, state.Policy.Frozen),
            state.PolicyRevision,
            p is null ? null : new(p.PreparationId.ToString("D"), p.DraftId.ToString("D"), p.BaseVersion, p.Purpose, p.Status.ToString(),
                p.Approvals.Select(a => new HarnessApprovalResponse(a.ApprovalId.ToString("D"), a.ActionHash, Operation(a.Operation), a.Status)).ToArray(),
                p.Evidence.Select(e => new HarnessEvidenceResponse(e.Actor, e.DraftRevision, e.Check, e.Status.ToString(), e.Expected, e.Observed, e.Limitation)).ToArray(),
                p.PublishedVersion, p.DiagnosticId?.ToString("D"), p.PublishedDraftRevision),
            review.Draft?.Revision, review.Draft?.Candidate.SystemInstructions,
            review.Draft?.Candidate.SkillList.Select(Skill).ToArray() ?? [],
            review.Draft?.Candidate.Environment?.KnowledgeList.Select(k => new HarnessKnowledgeResponse(k.Identity, k.Title, k.Citation, k.ResourcePath)).ToArray() ?? [],
            review.Draft?.Candidate.Environment?.ToolList ?? [], p?.PublishedChanges is { } changes ? new AdminDefinitionDraftDiffResponse(p.DraftId.ToString("D"), p.PublishedDraftRevision!.Value, "PublishedCandidate", p.BaseVersion,
                changes.Select(s => new AdminDefinitionDiffSectionResponse(s.SectionId, s.Label, s.ChangeKind, s.BeforeSummary, s.AfterSummary)).ToArray())
                : review.Diff is null ? null : AdminHttpMapping.ToDiff(review.Diff),
            review.Resources.Select(AdminHttpMapping.ToDraftResource).ToArray());
    }
}
