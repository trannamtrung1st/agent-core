using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Evaluations;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task Unified_model_judge_uses_pinned_route_tool_free_http_and_never_replays(bool sqlite, bool malformed)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var setup = await ProviderSetup(f, ModelProviderKind.OpenAICompatible);
        var response = malformed ? "invalid judge output" : "{\"outcome\":\"Passed\"}";
        var delta = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = response }, finish_reason = (string?)null } } });
        using var http = new ProviderHttpFixture("data: " + delta + "\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        await using var services = RuntimeServices(f, http, new()); var tenant = setup.Route.TenantId; var human = setup.Human; var actor = human.PrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var defs = services.GetRequiredService<TenantDefinitionService>();
        var evaluator = new EvaluationModelEvaluator((ITenantModelStore)f.Iam, new LanguageModelResolver(ProviderFactory(f, http)), f.Authorizer);
        var service = new TenantEvaluationService(f.Iam, (ITenantEvaluationStore)f.Iam, (ITenantDefinitionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam,
            f.Authorizer, f.Identity, f.Ids, f.Clock, services.GetRequiredService<AgentDefinitionDraftSyntheticEvaluationRunner>(), services.GetRequiredService<IDefinitionResourceContentStore>(), evaluator);
        var draft = await defs.CreateAsync(actor, tenant, "Judge subject", await Revision(), initiator: human);
        var suite = await service.SaveDraftSuiteAsync(actor, tenant, draft.DraftId, 1, new("Judge criteria", [new("judge", "Check saved metadata", EvaluatorKind.Agent, true, Rubric: "The source must contain a concrete saved Definition revision and fingerprint.", ModelOfferingId: setup.Offering.OfferingId)], 0, await Revision()), human);
        var request = new EvaluationRequest(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, suite.DraftRevision, draft.DraftId, 1, "judge-once", await Revision());
        var result = await service.RunAsync(actor, tenant, request, human);
        Assert.Equal(malformed ? EvaluationOutcome.Inconclusive : EvaluationOutcome.Passed, result.Outcome);
        Assert.Equal(malformed ? "evaluator_unavailable" : "model_judgment", Assert.Single(result.Findings).ReasonCode);
        Assert.Equal(setup.Route, Assert.Single(result.ModelPins!).Binding.Route);
        var sent = Assert.Single(http.Requests); Assert.True(sent.HasDispatchAuthority); Assert.False(sent.Body.TryGetProperty("tools", out _));
        Assert.Equal(result.AttemptId, (await service.RunAsync(actor, tenant, request, human)).AttemptId); Assert.Single(http.Requests);
        Assert.DoesNotContain("factory-private-key", JsonSerializer.Serialize(result)); Assert.DoesNotContain("concrete saved Definition revision", JsonSerializer.Serialize(result.Findings));
    }
}
