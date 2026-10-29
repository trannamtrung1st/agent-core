using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class FreshConfigurationModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); await Task.Yield();
            Assert.Contains(request.Messages, m => m.Text.Contains("Explicit adopted task configuration.", StringComparison.Ordinal));
            if ((request.Tools ?? []).Any(t => t.Name == ToolCatalog.WorkComplete))
            {
                yield return new ModelToolCallEvent(new("fresh-config-done", ToolCatalog.WorkComplete,
                    """{"outcome":"Response","summary":"The exact adopted configuration completed.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelTextDelta("The exact adopted configuration completed.");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Fresh_tenant_occurrence_pins_adopted_publication_settings_and_resources_while_preserving_existing_Session_model(bool sqlite, bool existingSession)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: new FreshConfigurationModel(), servicesOverride: services => {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            foreach (var host in services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType is { } type && (type == typeof(AgentCore.Api.AgentRunHostedService) || type == typeof(AgentCore.Api.TriggerSchedulerHostedService))).ToArray()) services.Remove(host);
        });
        using var owner = ConnectedClient(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Canonical occurrence configuration", expectedRevision = await Revision(owner, tenant) }));
        var definition = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid();
        var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        candidate["agentInstructions"] = "Original immutable Session configuration.";
        candidate["triggerPolicy"] = JsonSerializer.SerializeToNode((await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 35))!.TriggerPolicy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var saved = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
        await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = saved.GetProperty("revision").GetInt64() }));
        var created = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = definition, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var principal = created.GetProperty("principalId").GetGuid(); var path = api + $"/agents/{instance}";
        await TenantSessionAccessTests.GrantNativePrivateSpaceRead(owner, tenant, instance);
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance,
            modelPreference = new { mode = "fixed", offeringId = "dca30000-0000-4000-8000-000000000001", reasoning = new { mode = "fixed", effort = "high" } },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var original = (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot;
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(path + "/settings/conversationPolicy", new { set = new { maxOutputTokens = 2048 }, clear = Array.Empty<string>(), expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
        var resource = (await Json(await owner.PostAsJsonAsync(path + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
            input = new { logicalPath = "occurrence/current.md", kind = 1, mediaType = "text/markdown", content = Encoding.UTF8.GetBytes("Current occurrence knowledge."), enabled = true } }))).GetProperty("resource");
        var fork = await Json(await owner.PostAsJsonAsync(api + $"/agent-definitions/{definition}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) }));
        draftId = fork.GetProperty("draftId").GetGuid(); candidate = JsonNode.Parse(fork.GetProperty("candidate").GetRawText())!;
        candidate["agentInstructions"] = "Explicit adopted task configuration.";
        saved = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
        await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = saved.GetProperty("revision").GetInt64() }));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/active-version", new { version = 2, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 })).StatusCode);
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Current canonical occurrence executor",
            ["automations.run", "agent-runs.create", "agents.read", "resources.read", "agents.chat", "sessions.read", "agent-runs.read", .. existingSession ? Array.Empty<string>() : new[] { "sessions.create" }], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, principal, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        await Json(await owner.PostAsJsonAsync(path + "/automations", new { enabled = true, name = "Adopted configuration task", instructions = "Complete once with the exact current configuration.", requiresTools = true,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(1).ToString("O") },
            targetSessionId = existingSession ? (Guid?)session : null, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4 }));
        clock.Advance(TimeSpan.FromMinutes(2)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        var run = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        Assert.Equal(2, run.DefinitionVersion); Assert.Equal(4, run.Admission.Configuration!.InstanceRevision);
        Assert.Equal(2048, run.Admission.Configuration.Definition.ConversationPolicy.MaxOutputTokens);
        Assert.Contains(run.Admission.Configuration.Resources, r => r.VirtualPath == resource.GetProperty("virtualPath").GetString());
        Assert.Equal(ExecutionOrigin.ScheduledOccurrence, run.Admission.ExecutionContext!.Origin); Assert.Null(run.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        Assert.Equal(existingSession, run.SessionId == session); if (existingSession) Assert.Equal("high", run.PinnedModel.ReasoningEffort);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!run.IsTerminal)
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
            run = (await runs.FindTenantRunAsync(tenant, run.AgentRunId, deadline.Token))!;
            if (!run.IsTerminal) await Task.Delay(20, deadline.Token);
        }
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure}");
        var retained = (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot;
        Assert.Equal(1, retained.Definition.Version); Assert.Equal(original.SpeechLocaleOverride, retained.SpeechLocaleOverride);
        Assert.Equal(JsonSerializer.Serialize(original.ModelSelection), JsonSerializer.Serialize(retained.ModelSelection)); Assert.Equal("high", retained.ModelSelection!.ReasoningEffort);
    }
}
