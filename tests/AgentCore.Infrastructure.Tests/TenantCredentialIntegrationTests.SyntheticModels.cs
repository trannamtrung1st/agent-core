using AgentCore.Application.Models;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Testing;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(SyntheticModelCatalog.ScriptedAlphaOfferingId, false)]
    [InlineData(SyntheticModelCatalog.ScriptedBetaOfferingId, false)]
    [InlineData(SyntheticModelCatalog.ScriptedVisionOfferingId, false)]
    [InlineData(SyntheticModelCatalog.ScriptedAlphaOfferingId, true)]
    public async Task DynamicModels_Synthetic_fixed_offering_executes_with_a_tenant_binding_and_no_credentials(string offeringId, bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human,
            SessionMode.Text, await Revision(), agent.Instance.Revision, preference: offeringId == SyntheticModelCatalog.ScriptedAlphaOfferingId ? null : AgentCore.Domain.Models.ModelPreference.Fixed(offeringId));
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Hello", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Equal(offeringId, run.PinnedModel.OfferingId);
        Assert.Equal(tenant, run.PinnedModel.Binding!.Route.TenantId);
        Assert.Equal(ModelMetadataSource.SyntheticFixture, run.PinnedModel.Binding.Capabilities.Source);
        Assert.Null(run.PinnedModel.Binding.Route.CredentialId);
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var output = new CapturingSessionOutput();
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output,
            frame, storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>());
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token);
        await runtime.WaitUntilIdleAsync(deadline.Token);
        var completed = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status); Assert.Equal("Hello from synthetic.", completed.Result!.Text);
        Assert.Empty((await f.Credentials.ListTenantCredentialsAsync(tenant, null, 0, 30)).Items);
        Assert.Empty((await ((ITenantModelStore)f.Iam).ReadTenantModelsAsync(tenant)).Connections); Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_Synthetic_actual_Runtime_admits_configured_default_executes_tool_and_retains_reply(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var setup = await ProviderSetup(f, ModelProviderKind.OpenAI);
        var tenant = setup.Route.TenantId; var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await Models(f).SaveDefaultAsync(setup.Human, tenant, setup.Offering.OfferingId, "high", await Revision(), 0);
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human,
            SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(),
            ScriptedLanguageModel.AuthorizationAuditMarker + " agents.read " + agent.Instance.InstanceId.ToString("D"), await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Equal(setup.Route, run.PinnedModel.Binding!.Route); Assert.Equal("high", run.PinnedModel.ReasoningEffort);
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = new CapturingSessionOutput();
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!,
            output, frame, storage, runs, services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>());
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token);
        await runtime.WaitUntilIdleAsync(deadline.Token);
        var completed = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal("Synthetic authorization check completed. Its result is recorded in the organization audit.", completed.Result!.Text);
        Assert.Equal(setup.Route, completed.PinnedModel.Binding!.Route);
        Assert.Contains(await f.Iam.ReadAuditAsync(tenant, 100), audit => audit.Execution?.Context.RunId == run.AgentRunId && audit.Execution.Permission == "agents.read");
        Assert.Empty(http.Requests); // RejectNetwork also refuses every unrelated client dispatch.
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var retained = (await reopened.FindTenantRunAsync(tenant, run.AgentRunId))!;
            Assert.Equal(AgentRunStatus.Completed, retained.Status);
            Assert.Equal(completed.Result.Text, retained.Result!.Text);
            Assert.Equal(completed.Result.OutcomeEntryId, retained.Result.OutcomeEntryId);
            Assert.Equal(completed.PinnedModel.Binding, retained.PinnedModel.Binding);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_Synthetic_uses_explicit_tenant_default_and_never_recovers_to_a_fixture(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.OpenAI);
        var tenant = setup.Route.TenantId; var store = (ITenantModelStore)f.Iam;
        var fixture = SyntheticModelCatalog.Create(); var reader = new SyntheticTenantModelCatalogReader(f.Iam, store);
        async Task<AgentCore.Domain.Security.TenantSecurityState> State() => (await f.Iam.ReadTenantAsync(tenant))!;
        var unconfigured = await reader.ReadAsync(await State());
        Assert.Equal(fixture.DefaultOfferingId, unconfigured.DefaultOfferingId);
        Assert.NotNull(unconfigured.Get(setup.Offering.OfferingId.ToString("D")));
        var stale = await State();
        await Models(f).SaveDefaultAsync(setup.Human, tenant, setup.Offering.OfferingId, "high", stale.Tenant.Revision, 0);
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await reader.ReadAsync(stale));
        var configured = await reader.ReadAsync(await State());
        var selection = SessionModelBinder.PinDefault(configured);
        Assert.Equal(setup.Route, selection.Binding!.Route); Assert.Equal("high", selection.ReasoningEffort);
        Assert.Equal(ModelSelectionSource.TenantDefault, selection.SelectionSource);
        Assert.NotNull(configured.Get(fixture.DefaultOfferingId)); // Explicit offline choices remain selectable.
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var retained = await new SyntheticTenantModelCatalogReader(reopened, reopened).ReadAsync(await State());
            Assert.Equal(selection, SessionModelBinder.PinDefault(retained));
        }
        await f.Service.UpdateAsync(setup.Human.PrincipalId, tenant, setup.Connection.CredentialId!.Value,
            new("Unavailable model key", CredentialStatus.Disabled, null, []), (await State()).Tenant.Revision, 1, initiator: setup.Human);
        var unavailable = await reader.ReadAsync(await State());
        Assert.Null(unavailable.Get(selection.OfferingId)); Assert.NotEmpty(unavailable.Models);
        Assert.Equal("ModelUnavailable", Assert.Throws<AgentCoreException>(() => SessionModelBinder.PinDefault(unavailable)).Code);
        // An explicit human fixture choice is distinct from recovering a failed default.
        Assert.Equal(fixture.DefaultOfferingId, SessionModelBinder.Bind(unavailable, fixture.DefaultOfferingId, null, ModelSelectionSource.User).OfferingId);
        await Models(f).SaveDefaultAsync(setup.Human, tenant, null, null, (await State()).Tenant.Revision, 1);
        var cleared = await reader.ReadAsync(await State());
        Assert.Equal("", cleared.DefaultOfferingId);
        Assert.Equal("ModelUnavailable", Assert.Throws<AgentCoreException>(() => SessionModelBinder.PinDefault(cleared)).Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_Synthetic_resolver_uses_scripted_output_with_exact_tenant_authority_and_revocation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.Anthropic);
        var binding = new ModelBinding(setup.Route, setup.Offering.Capabilities, setup.Offering.Label, setup.Connection.Label);
        var selection = SessionModelBinder.Bind(ModelDescriptor.FromBinding(binding), "high", ModelSelectionSource.User);
        var admitted = Invocation(setup.Route);
        var invocation = new ModelInvocation(admitted.Context!, admitted.Route, admitted.RequireCurrent, binding);
        var resolver = new SyntheticLanguageModelResolver(new ScriptedLanguageModel(),
            new TenantModelRouteAuthority(f.Iam, (ITenantModelStore)f.Iam));
        Assert.Throws<AccessDeniedException>(() => resolver.Resolve(selection, ModelPurpose.Conversation, null));
        var model = resolver.Resolve(selection, ModelPurpose.Conversation, invocation);
        var result = await ProviderEvents(model, ProviderRequest());
        Assert.DoesNotContain(result, item => item is ModelFailed);
        Assert.Equal("Hello from synthetic.", Assert.Single(result.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
        await f.Service.UpdateAsync(setup.Human.PrincipalId, setup.Route.TenantId, setup.Connection.CredentialId!.Value,
            new("Disabled model key", CredentialStatus.Disabled, null, []), (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!.Tenant.Revision, 1, initiator: setup.Human);
        var denied = await ProviderEvents(model, ProviderRequest());
        Assert.Single(denied.OfType<ModelFailed>());
        Assert.DoesNotContain(denied, item => item is ModelSemanticResponseReady or ModelToolCallEvent or ModelCompleted);
    }
}
