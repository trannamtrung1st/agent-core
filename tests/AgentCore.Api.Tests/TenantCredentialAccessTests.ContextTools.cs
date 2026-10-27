using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class ContextToolsModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public TaskCompletionSource<ModelRequest> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ModelRequest> AfterLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Lookup;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Entered.TrySetResult(request)) await Release.Task.WaitAsync(cancellationToken);
            if (Lookup)
            {
                if (request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == "variables.resolve") is not { } resolved)
                {
                    yield return new ModelToolCallEvent(new("lookup-plain", "variables.resolve", "{\"name\":\"REPORT_REGION\"}"));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                AfterLookup.TrySetResult(request); await Finish.Task.WaitAsync(cancellationToken);
                using var value = JsonDocument.Parse(resolved.Text);
                var answer = value.RootElement.GetProperty("result").GetProperty("value").GetString()!;
                yield return new ModelSemanticResponseReady(new(answer, new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public Task Context_tools_follow_gate_selection_and_current_IAM_without_exposing_protected_values(bool sqlite, int state) => ContextToolsWorkflow(sqlite, state);
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Plain_variable_model_lookup_is_fenced_again_at_final_output(bool sqlite, bool changed) => ContextToolsWorkflow(sqlite, 2, true, changed);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task Protected_variable_descriptors_require_current_binding_and_IAM_without_revealing_values(bool sqlite) => ContextToolsWorkflow(sqlite, 2, protectedRefs: true);
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public Task Plain_variable_delete_recreate_cannot_reuse_an_old_revision_one_output_witness(bool sqlite,bool sameValue) =>
        ContextToolsWorkflow(sqlite,2,lookup:true,changed:true,recreated:true,sameValue:sameValue);
    private async Task ContextToolsWorkflow(bool sqlite, int state, bool lookup = false, bool changed = false, bool protectedRefs = false, bool recreated = false, bool sameValue = false)
    {
        var model = new ContextToolsModel { Lookup = lookup }; await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        using var owner = ConnectedClient(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var principal = agent.GetProperty("principalId").GetGuid();
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Explicit context tools", ["spaces.read", "environments.read", "environments.activate", "variables.read", "variables.resolve", "sessions.read", "sessions.manage", "agents.read", "agents.chat"], await Revision(owner, tenant));
        var grant = await access.AssignRoleAsync(human, tenant, principal, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var spaceService = factory.Services.GetRequiredService<TenantSpaceService>(); var environmentService = factory.Services.GetRequiredService<TenantEnvironmentService>();
        var space = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance))!;
        if (state > 0) space = await spaceService.UpdateAsync(human, tenant, space.SpaceId, space.Name, "", "", true, await Revision(owner, tenant), space.Revision);
        var profile = await environmentService.SaveAsync(human, tenant, space.SpaceId, Guid.NewGuid(), "On demand", "Safe profile", await Revision(owner, tenant), space.Revision, 0);
        await environmentService.SaveVariableAsync(human, tenant, space.SpaceId, profile.EnvironmentId, "REPORT_REGION", EnvironmentVariableKind.Plain, "Plain region", "authorized-plain-value", null, null, await Revision(owner, tenant), space.Revision, profile.Revision, 0);
        profile = await environmentService.ReadAsync(human, tenant, space.SpaceId, profile.EnvironmentId);
        Guid? secretId = null, credentialId = null;
        if (protectedRefs)
        {
            secretId = (await factory.Services.GetRequiredService<TenantSecretService>().CreateAsync(human, tenant, "Private reference", "", "never-visible-secret-value", await Revision(owner, tenant))).SecretId;
            var secretAccessEditor = await Json(await owner.GetAsync($"{api}/access/resources/secret/{secretId}/editor"));
            Assert.Equal("Private reference", secretAccessEditor.GetProperty("name").GetString());
            Assert.True(secretAccessEditor.GetProperty("canAssign").GetBoolean());
            Assert.DoesNotContain("never-visible-secret-value", secretAccessEditor.GetRawText(), StringComparison.Ordinal);
            credentialId = (await factory.Services.GetRequiredService<TenantCredentialService>().CreateAsync(human, tenant,
                new("Reference account", CredentialKind.Password, CredentialPurpose.General, new Dictionary<string, string> { ["username"] = "scoped-user" }, ["https://login.example.test"], "never-visible-credential-value"), await Revision(owner, tenant))).Credential.CredentialId;
            var resourceRole = await access.SaveRoleAsync(human, tenant, null, "Exact variable resources", ["credentials.read", "credentials.use", "secrets.read", "secrets.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, principal, resourceRole.RoleId, TenantCredentialRules.Scope(tenant, credentialId.Value), await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, principal, resourceRole.RoleId, TenantSecretService.Scope(tenant, secretId.Value), await Revision(owner, tenant));
            await spaceService.SetMemberAsync(human, tenant, space.SpaceId, instance, "", "", [], [new(SpaceBindingKind.Secret, secretId.Value.ToString("D")), new(SpaceBindingKind.Credential, credentialId.Value.ToString("D"), "variable-account")], await Revision(owner, tenant), space.Revision, 1);
            space = await spaceService.ReadAsync(human, tenant, space.SpaceId);
            await environmentService.SaveVariableAsync(human, tenant, space.SpaceId, profile.EnvironmentId, "LOGIN", EnvironmentVariableKind.CredentialReference, "Account reference", null, null, credentialId, await Revision(owner, tenant), space.Revision, profile.Revision, 0);
            profile = await environmentService.ReadAsync(human, tenant, space.SpaceId, profile.EnvironmentId);
            await environmentService.SaveVariableAsync(human, tenant, space.SpaceId, profile.EnvironmentId, "API_SECRET", EnvironmentVariableKind.SecretReference, "Unsupported generic secure use", null, secretId, null, await Revision(owner, tenant), space.Revision, profile.Revision, 0);
            profile = await environmentService.ReadAsync(human, tenant, space.SpaceId, profile.EnvironmentId);
        }
        var created = await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync()); var session = (await Json(created)).GetProperty("sessionId").GetGuid();
        var context = await Json(await owner.GetAsync($"{api}/sessions/{session}/context"));
        if (state == 2)
        {
            var selected = await owner.PostAsJsonAsync($"{api}/sessions/{session}/context/switch", new { transitionId = Guid.NewGuid(), spaceId = space.SpaceId, environmentId = profile.EnvironmentId,
                expectedRevision = await Revision(owner, tenant), expectedSessionRevision = context.GetProperty("sessionRevision").GetInt64(), expectedSelectionRevision = 1 });
            Assert.True(selected.IsSuccessStatusCode, await selected.Content.ReadAsStringAsync()); context = await Json(await owner.GetAsync($"{api}/sessions/{session}/context"));
        }
        var submit = await owner.PostAsJsonAsync($"{api}/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect this context only", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = context.GetProperty("sessionRevision").GetInt64() });
        Assert.True(submit.StatusCode == HttpStatusCode.Accepted, await submit.Content.ReadAsStringAsync()); var runId = (await Json(submit)).GetProperty("runId").GetGuid();
        try
        {
            var request = await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var offered = (request.Tools ?? []).Select(t => t.Name).ToArray();
            Assert.Contains("spaces.list", offered); Assert.Contains("spaces.activate", offered);
            Assert.Equal(state > 0, offered.Contains("environments.list")); Assert.Equal(state == 2, offered.Contains("variables.resolve"));
            Assert.DoesNotContain("authorized-plain-value", JsonSerializer.Serialize(request));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var run = (await runs.FindTenantRunAsync(tenant, runId))!;
            if (lookup)
            {
                model.Release.TrySetResult(); var observed = await model.AfterLookup.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Contains(observed.Messages, m => m.Role == ModelRole.Tool && m.Name == "variables.resolve" && m.Text.Contains("authorized-plain-value", StringComparison.Ordinal));
                if (recreated)
                {
                    await environmentService.DeleteVariableAsync(human,tenant,space.SpaceId,profile.EnvironmentId,"REPORT_REGION",await Revision(owner,tenant),space.Revision,profile.Revision,1);
                    var afterDelete=await environmentService.ReadAsync(human,tenant,space.SpaceId,profile.EnvironmentId);
                    await environmentService.SaveVariableAsync(human,tenant,space.SpaceId,profile.EnvironmentId,"REPORT_REGION",EnvironmentVariableKind.Plain,"Recreated source",sameValue?"authorized-plain-value":"recreated-plain-value",null,null,await Revision(owner,tenant),space.Revision,afterDelete.Revision,0);
                    var recreatedValue=Assert.Single(await factory.Services.GetRequiredService<ITenantEnvironmentStore>().ReadVariablesAsync(tenant,space.SpaceId,profile.EnvironmentId),v=>v.Name=="REPORT_REGION");
                    Assert.Equal(1,recreatedValue.Revision);
                }
                else if (changed)
                {
                    var update = await owner.PutAsJsonAsync($"{api}/spaces/{space.SpaceId}/environments/{profile.EnvironmentId}/variables/REPORT_REGION", new {
                        kind = "Plain", description = "Changed while generating", plainValue = "new-plain-value", expectedRevision = await Revision(owner, tenant),
                        expectedSpaceRevision = space.Revision, expectedEnvironmentRevision = profile.Revision, expectedVariableRevision = 1 });
                    Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
                }
                model.Finish.TrySetResult(); var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                do { run = (await runs.FindTenantRunAsync(tenant, runId))!; if (run.IsTerminal) break; await Task.Delay(20); } while (DateTimeOffset.UtcNow < deadline);
                Assert.Equal(changed ? AgentRunStatus.Failed : AgentRunStatus.Completed, run.Status);
                var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items;
                if (changed) Assert.DoesNotContain(history, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed && e.Text.Contains("authorized-plain-value", StringComparison.Ordinal));
                else Assert.Contains(history, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed && e.Text.Contains("authorized-plain-value", StringComparison.Ordinal));
                Assert.Equal(0, factory.Outbound); return;
            }
            var frame = new TenantRuntimeFrame(run.Owner, session, runId, run.Claim!.Generation); var service = factory.Services.GetRequiredService<TenantContextToolService>();
            var list = await service.ExecuteAsync(frame, new("context-list", "spaces.list", "{}"), 16000); Assert.Contains(space.SpaceId.ToString("D"), list);
            if (protectedRefs)
            {
                var account = await service.ExecuteAsync(frame, new("resolve-account", "variables.resolve", "{\"name\":\"LOGIN\"}"), 16000);
                Assert.Contains("variable-account", account); Assert.Contains("authorizationRequiredAtConsumer", account);
                var secret = await service.ExecuteAsync(frame, new("resolve-secret", "variables.resolve", "{\"name\":\"API_SECRET\"}"), 16000);
                Assert.Contains("unsupported_secure_use", secret);
                var secretAccess = await Json(await owner.GetAsync($"{api}/secrets/{secretId}/access"));
                Assert.Contains(secretAccess.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "secrets.manage");
                var hidden = await owner.GetAsync($"{api}/spaces/{space.SpaceId}/environments/{profile.EnvironmentId}/variables/API_SECRET");
                Assert.True(hidden.IsSuccessStatusCode, await hidden.Content.ReadAsStringAsync());
                Assert.DoesNotContain("never-visible-secret-value", await hidden.Content.ReadAsStringAsync());
                var visible = await Json(await owner.GetAsync($"{api}/spaces/{space.SpaceId}/environments/{profile.EnvironmentId}/variables/REPORT_REGION"));
                Assert.Equal("authorized-plain-value", visible.GetProperty("plainValue").GetString());
                Assert.DoesNotContain("never-visible-secret-value", secret); Assert.DoesNotContain("never-visible-credential-value", account);
                var metadata = await service.ExecuteAsync(frame, new("protected-list", "variables.list", "{}"), 16000);
                Assert.DoesNotContain("never-visible", metadata); Assert.DoesNotContain("authorized-plain-value", metadata);
                var member = (await factory.Services.GetRequiredService<ITenantSpaceStore>().ReadMembershipsAsync(tenant, space.SpaceId)).Single(m => m.InstanceId == instance);
                await spaceService.SetMemberAsync(human, tenant, space.SpaceId, instance, member.Responsibility, member.Instructions, member.DisabledInheritedBindingKeys,
                    member.AddedBindings.Where(b => b.Kind != SpaceBindingKind.Credential).ToArray(), await Revision(owner, tenant), space.Revision, member.Revision);
                await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(frame, new("resolve-unbound", "variables.resolve", "{\"name\":\"LOGIN\"}"), 16000).AsTask());
                var secretService = factory.Services.GetRequiredService<TenantSecretService>();
                await secretService.UpdateAsync(human, tenant, secretId!.Value, "Private reference", "", SecretStatus.Disabled, await Revision(owner, tenant), 1);
                await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(frame, new("resolve-disabled", "variables.resolve", "{\"name\":\"API_SECRET\"}"), 16000).AsTask());
                Assert.Contains(run.Admission.Configuration!.SpaceBindings, b => b.Kind == SpaceBindingKind.Credential && b.ResourceKey == credentialId!.Value.ToString("D"));
                Assert.Equal(0, factory.Outbound); return;
            }
            if (state < 2)
            {
                var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(frame, new("resolve-denied", "variables.resolve", "{\"name\":\"REPORT_REGION\"}"), 16000).AsTask());
                Assert.Equal(state == 0 ? "environments_disabled" : "environment_not_selected", denied.ReasonCode);
            }
            else
            {
                var variables = await service.ExecuteAsync(frame, new("variable-list", "variables.list", "{}"), 16000); Assert.Contains("REPORT_REGION", variables); Assert.DoesNotContain("authorized-plain-value", variables);
                var value = await service.ExecuteAsync(frame, new("variable-resolve", "variables.resolve", "{\"name\":\"REPORT_REGION\"}"), 16000); Assert.Contains("authorized-plain-value", value);
                var switchResult = await service.ExecuteAsync(frame, new("deactivate", "environments.deactivate", "{\"expectedSelectionRevision\":2}"), 16000);
                Assert.Contains("pending_next_turn", switchResult);
                Assert.Equal(profile.EnvironmentId, (await runs.FindTenantRunAsync(tenant, runId))!.Admission.Configuration!.SpaceContext!.EnvironmentId);
                Assert.Null((await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot.ContextSelection!.EnvironmentId);
                Assert.Equal(switchResult, await service.ExecuteAsync(frame, new("deactivate", "environments.deactivate", "{\"expectedSelectionRevision\":2}"), 16000));
            }
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{grant.AssignmentId}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(frame, new("context-after-revoke", "spaces.list", "{}"), 16000).AsTask());
            Assert.Equal(0, factory.Outbound);
        }
        finally { model.Release.TrySetResult(); model.Finish.TrySetResult(); }
    }
}
