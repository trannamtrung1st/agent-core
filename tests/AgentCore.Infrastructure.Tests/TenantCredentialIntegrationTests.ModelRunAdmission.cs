using AgentCore.Application.Models;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Application.Triggers;
using System.Text.Json;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_actual_tenant_admission_claim_and_reopen_preserve_route_and_fence_revoked_dispatch(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.Anthropic);
        var tenant = setup.Route.TenantId; var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var reader = new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam);
        var agents = (ITenantAgentStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var sessionService = new TenantSessionService(f.Iam, agents, sessions, runs, (ITenantDefinitionStore)f.Iam, f.BuiltIns,
            f.Identity, f.Authorizer, reader, new(), f.Ids, f.Clock);
        var before = await Revision();
        Assert.Equal("ModelUnavailable", (await Assert.ThrowsAsync<AgentCoreException>(async () => await sessionService.CreateAsync(tenant,
            agent.Instance.InstanceId, human, SessionMode.Text, before, agent.Instance.Revision))).Code);
        Assert.Equal(before, await Revision());
        await Models(f).SaveDefaultAsync(setup.Human, tenant, setup.Offering.OfferingId, "low", await Revision(), 0);
        var session = await sessionService.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var runService = new TenantRunService(f.Iam, sessions, runs, f.RunConfiguration(), f.Identity, f.Authorizer, reader, f.Ids, f.Clock);
        var receipt = await runService.SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Use the admitted connection", await Revision(), 1);
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Equal(setup.Route, run.PinnedModel.Binding!.Route); Assert.Equal("low", run.PinnedModel.ReasoningEffort);
        var runtime = (ITenantRuntimeStore)f.Iam;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
            var retained = (await reopened.FindTenantRunAsync(tenant, run.AgentRunId))!;
            Assert.Equal(run.PinnedModel.Binding, retained.PinnedModel.Binding); run = retained;
            Assert.Equal(setup.Route, (await reopened.FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.ModelSelection!.Binding!.Route);
        }
        var authority = new TenantRunExecutionAuthority(new(f.Iam, runs, agents, f.Authorizer,
            new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)), f.Iam, f.Clock, f.RunConfiguration());
        var invocation = ModelInvocation.ForRun(run, setup.Route, authority);
        Assert.Throws<AccessDeniedException>(() => ModelInvocation.ForRun(run, setup.Route with { ConnectionRevision = 2 }, authority));
        using var http = new ProviderHttpFixture(FinalWire(setup.Route), NativeLanguageModelTests.FinalRound(setup.Route.Kind, """{"decision":"allow"}"""));
        var resolver = new LanguageModelResolver(ProviderFactory(f, http));
        var selection = new SessionModelSelection(run.PinnedModel.OfferingId, run.PinnedModel.ConnectionId, run.PinnedModel.ExternalModelId,
            ModelSelectionSource.Host, run.PinnedModel.ReasoningEffort, Binding: run.PinnedModel.Binding);
        Assert.Throws<AccessDeniedException>(() => resolver.Resolve(selection, ModelPurpose.Conversation, null));
        var model = resolver.Resolve(selection, ModelPurpose.Conversation, invocation);
        var result = await ProviderEvents(model, ProviderRequest() with { ReasoningEffort = run.PinnedModel.ReasoningEffort });
        Assert.DoesNotContain(result, e => e is ModelFailed); Assert.Single(result.OfType<ModelSemanticResponseReady>());
        var action = JsonSerializer.SerializeToElement(new { tool = "automation.create", arguments = new { instructions = "Review experience daily" } });
        var proposed = new AutomationAuthorizationRequest("Review experience daily", TriggerCommandAction.Create, "automation.create", action,
            AgentCore.Application.Tools.ToolActionHash.Compute("automation.create", action));
        var classifier = new TenantTriggerCommandAuthorizer(ProviderFactory(f, http), f.Clock);
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable, (await classifier.AuthorizeAsync(proposed)).Decision);
        Assert.Single(http.Requests);
        proposed = proposed with { Invocation = invocation };
        Assert.Equal(TriggerCommandAuthorizationDecision.Allow, (await classifier.AuthorizeAsync(proposed)).Decision);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("low", http.Requests[1].Body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(http.Requests[1].Body.TryGetProperty("tools", out _));
        Assert.False(http.Requests[1].Body.TryGetProperty("temperature", out _));
        await f.Service.UpdateAsync(human.PrincipalId, tenant, setup.Connection.CredentialId!.Value,
            new("Revoked inference", CredentialStatus.Disabled, null, []), await Revision(), 1, initiator: setup.Human);
        var denied = await ProviderEvents(model, ProviderRequest());
        Assert.Equal(ProviderErrorCode.Authentication, Assert.Single(denied.OfType<ModelFailed>()).Failure.Code);
        Assert.Equal(TriggerCommandAuthorizationDecision.ClassifierUnavailable, (await classifier.AuthorizeAsync(proposed)).Decision);
        Assert.Equal(2, http.Requests.Count);
        var retainedRun = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        Assert.Equal(run.PinnedModel.Binding, retainedRun.PinnedModel.Binding);
        Assert.Equal(run.Claim!.Generation, retainedRun.Claim!.Generation);
    }
}
