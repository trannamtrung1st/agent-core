using AgentCore.Application.Evaluations;
using AgentCore.Domain.Evaluations;

namespace AgentCore.Api.Security;

public static class TenantEvaluationEndpoints
{
    public sealed record PolicyRequest(long ExpectedRevision, long ExpectedPolicyRevision, IReadOnlyList<EvaluationBinding> Defaults);
    public sealed record DraftSuiteRequest(SuiteWrite Suite, long ExpectedDraftRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}").RequireAuthorization();
        group.MapGet("/governance/evaluation-policies", (HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.GovernanceAsync(a.PrincipalId, a.TenantId, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPut("/governance/evaluation-policies", (PolicyRequest body, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveGovernanceAsync(a.PrincipalId, a.TenantId, body.Defaults, body.ExpectedRevision, body.ExpectedPolicyRevision, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapGet("/evaluation-subjects/{kind}/{id:guid}", (EvaluationSubjectKind kind, Guid id, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SubjectAsync(a.PrincipalId, a.TenantId, kind, id, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapGet("/agents/{id:guid}/evaluation-bindings", (Guid id, HttpContext http, TenantEvaluationService service, AgentCore.Application.Security.TenantRunConfigurationResolver resolver, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(new { items = await service.InstanceBindingsAsync(a.PrincipalId, a.TenantId, id, IdentityHttp.TenantHuman(http), resolver, ct) });
        }));
        group.MapGet("/evaluation-suites", (int? page, int? pageSize, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SuitesAsync(a.PrincipalId, a.TenantId, page ?? 0, pageSize ?? 20, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPost("/evaluation-suites", (SuiteWrite body, HttpContext http, TenantEvaluationService service, AgentCore.Application.Ports.IIdGenerator ids, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveSuiteAsync(a.PrincipalId, a.TenantId, ids.NewId(), body, IdentityHttp.TenantHuman(http), ct), statusCode: 201);
        }));
        group.MapGet("/evaluation-suites/{suiteId:guid}", (Guid suiteId, int? version, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SuiteAsync(a.PrincipalId, a.TenantId, suiteId, version, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPost("/evaluation-suites/{suiteId:guid}/versions", (Guid suiteId, SuiteWrite body, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveSuiteAsync(a.PrincipalId, a.TenantId, suiteId, body, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPut("/definition-drafts/{draftId:guid}/evaluation-suite", (Guid draftId, DraftSuiteRequest body, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveDraftSuiteAsync(a.PrincipalId, a.TenantId, draftId, body.ExpectedDraftRevision, body.Suite, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapGet("/scripts", (int? page, int? pageSize, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ScriptsAsync(a.PrincipalId, a.TenantId, page ?? 0, pageSize ?? 20, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPost("/scripts", (ScriptWrite body, HttpContext http, TenantEvaluationService service, AgentCore.Application.Ports.IIdGenerator ids, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveScriptAsync(a.PrincipalId, a.TenantId, ids.NewId(), body, IdentityHttp.TenantHuman(http), ct), statusCode: 201);
        }));
        group.MapGet("/scripts/{scriptId:guid}", (Guid scriptId, int? version, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ScriptAsync(a.PrincipalId, a.TenantId, scriptId, version, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPost("/scripts/{scriptId:guid}/versions", (Guid scriptId, ScriptWrite body, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveScriptAsync(a.PrincipalId, a.TenantId, scriptId, body, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPost("/evaluation-attempts", (EvaluationRequest body, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.RunAsync(a.PrincipalId, a.TenantId, body, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapGet("/evaluation-attempts/{attemptId:guid}", (Guid attemptId, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(a.PrincipalId, a.TenantId, attemptId, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapGet("/evaluation-attempts", (EvaluationSubjectKind subjectKind, Guid subjectId, int? page, int? pageSize, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.AttemptsAsync(a.PrincipalId, a.TenantId, subjectKind, subjectId, page ?? 0, pageSize ?? 20, IdentityHttp.TenantHuman(http), ct));
        }));
        group.MapPost("/evaluation-attempts/{attemptId:guid}/review", (Guid attemptId, ManualAssessment body, HttpContext http, TenantEvaluationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReviewAsync(a.PrincipalId, a.TenantId, attemptId, body, IdentityHttp.TenantHuman(http), ct));
        }));
    }
}
