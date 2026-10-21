using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    private sealed class ProfileProbeModel(bool followup = false) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(["Profile-aware response completed."]);
        public ModelCapabilities Capabilities => inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold; public string? Answer;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); Entered.TrySetResult(); if (Hold) await Release.Task.WaitAsync(ct);
            if (followup && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AgentStateInspect))
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AgentStateInspect);
                yield return new ModelToolCallEvent(new("profile-state", ToolCatalog.AgentStateInspect, "{}")); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (Answer is { } text)
            { yield return new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.None, null), [])); yield return new ModelCompleted(ModelStopReason.Completed); }
            else await foreach (var value in inner.GenerateAsync(request, ct)) yield return value;
        }
    }
    private static async Task SetOwnProfile(HttpClient client, Guid tenant, string name)
    {
        var path = $"/api/v2/tenants/{tenant}/me/profile"; var current = await Json(await client.GetAsync(path));
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(path, new { expectedRevision = current.GetProperty("securityRevision").GetInt64(),
            expectedProfileRevision = current.GetProperty("profile").GetProperty("revision").GetInt64(), values = new { preferredName = name, locale = "vi-VN" } })).StatusCode);
    }
    private static async Task<(Guid Session, Guid Run)> SubmitProfileRequest(HttpClient client, Guid tenant, Guid instance, long instanceRevision = 1, Guid? existing = null)
    {
        var api = $"/api/v2/tenants/{tenant}";
        var session = existing ?? (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(client, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
        var current = await Json(await client.GetAsync(api + $"/sessions/{session}"));
        var response = await client.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Observe the currently owned Profile", expectedRevision = await Revision(client, tenant), expectedSessionRevision = current.GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); return (session, (await Json(response)).GetProperty("runId").GetGuid());
    }
    private static async Task<Guid> ProfileExecutor(HttpClient owner, Guid tenant, Guid instance, bool memory = true, bool browser = false)
    {
        var api = $"/api/v2/tenants/{tenant}"; var agent = await Json(await owner.GetAsync(api + $"/agents/{instance}"));
        var permissions = new List<string> { "agents.chat", "sessions.read", "agent-runs.read" }; if (memory) permissions.Add("memory.read"); if (browser) permissions.Add("browser.read");
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Profile executor", permissions, expectedRevision = await Revision(owner, tenant) }));
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = agent.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        return role.GetProperty("roleId").GetGuid();
    }
    private static async Task<AgentRun> ProfileTerminal(WebApplicationFactory<Program> factory, Guid tenant, Guid id)
    {
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        for (var i = 0; i < 500; i++) { var run = (await runs.FindTenantRunAsync(tenant, id))!; if (run.IsTerminal) return run; await Task.Delay(25); }
        throw new TimeoutException("The Profile scenario did not settle its actual Run.");
    }

    [Fact(Timeout = 90000)]
    public async Task Tenant_Run_Profile_actual_SQLite_host_restart_retains_metadata_and_reloads_the_current_requester()
    {
        var model = new ProfileProbeModel(); await using var first = new RuntimeStateFactory(true, model: model);
        using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(owner); await SetOwnProfile(owner, tenant, "DURABLE_PROFILE_REQUESTER"); await ProfileExecutor(owner, tenant, instance);
        var submitted = await SubmitProfileRequest(owner, tenant, instance); await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var terminal = await ProfileTerminal(first, tenant, submitted.Run); Assert.Equal(AgentRunStatus.Completed, terminal.Status);
        var source = AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).TenantProfileSource; Assert.NotNull(source);
        await first.DisposeAsync();
        var nextModel = new ProfileProbeModel(); await using var reopened = new RuntimeStateFactory(true, model: nextModel, root: first.Root);
        using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        await Login(again, "owner@example.test", "explicit-synthetic-test-password");
        var retained = (await reopened.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
        Assert.Equal(source, AgentRunToolCallCheckpoint.ReadExecutionState(retained.Checkpoint).TenantProfileSource);
        await reopened.Services.GetRequiredService<TenantRunProfileService>().RequireAsync(retained, source);
        var next = await SubmitProfileRequest(again, tenant, instance, existing: submitted.Session); await reopened.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        Assert.Equal(AgentRunStatus.Completed, (await ProfileTerminal(reopened, tenant, next.Run)).Status);
        Assert.Contains(Assert.Single(nextModel.Requests).Context!.Sections, s => s.Id == ContextSectionId.Profile && s.Message.Text.Contains("DURABLE_PROFILE_REQUESTER", StringComparison.Ordinal));
        Assert.Single(model.Requests);
    }

    [Theory(Timeout = 90000)] [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "missing")] [InlineData(true, "missing")] [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "off")] [InlineData(true, "off")]
    public async Task Tenant_Run_Profile_actual_model_uses_only_permitted_requester_Evidence_without_seeding_or_implicit_consent(bool sqlite, string mode)
    {
        var model = new ProfileProbeModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        await SetOwnProfile(owner, tenant, "friend"); await ProfileExecutor(owner, tenant, instance, mode != "missing");
        if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/memory.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        var revision = 1;
        if (mode == "off") { Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, set = new { profileEvidence = false }, clear = Array.Empty<string>() })).StatusCode); revision++; }
        var submitted = await SubmitProfileRequest(owner, tenant, instance, revision); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var terminal = await ProfileTerminal(factory, tenant, submitted.Run); Assert.Equal(AgentRunStatus.Completed, terminal.Status);
        var request = Assert.Single(model.Requests); var profile = request.Context!.Sections.SingleOrDefault(s => s.Id == ContextSectionId.Profile);
        if (mode == "allowed")
        {
            Assert.NotNull(profile); Assert.Equal(ContextLayer.Evidence, profile.Layer); Assert.Contains("friend", profile.Message.Text);
            Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).TenantProfileSource);
            Assert.DoesNotContain(request.Context.Sections, s => s.Layer != ContextLayer.Evidence && s.Message.Text.Contains("friend", StringComparison.Ordinal));
        }
        else { Assert.Null(profile); Assert.Null(AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).TenantProfileSource); }
        Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
        Assert.Equal("friend", (await Json(await owner.GetAsync(api + "/me/profile"))).GetProperty("profile").GetProperty("preferences").GetProperty("preferredName").GetProperty("value").GetString());
    }

    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Run_Profile_live_exclusion_refreshes_the_followup_and_preserves_one_Runtime_fact_and_historical_tool_Evidence(bool sqlite)
    {
        var model = new ProfileProbeModel(true); await using var factory = new RuntimeStateFactory(sqlite, model: model); factory.Browser.CountPages = true;
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        await SetOwnProfile(owner, tenant, "LIVE_PROFILE_SENTINEL"); await ProfileExecutor(owner, tenant, instance, browser: true);
        factory.Browser.BeforeRead = async n => {
            if (n != 3) return;
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                set = new { profileEvidence = false }, clear = Array.Empty<string>() })).StatusCode);
        };
        var submitted = await SubmitProfileRequest(owner, tenant, instance); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        Assert.Equal(AgentRunStatus.Completed, (await ProfileTerminal(factory, tenant, submitted.Run)).Status);
        var requests = model.Requests.ToArray(); Assert.Equal(2, requests.Length);
        Assert.Contains(requests[0].Context!.Sections, s => s.Id == ContextSectionId.Profile && s.Message.Text.Contains("LIVE_PROFILE_SENTINEL", StringComparison.Ordinal));
        Assert.DoesNotContain(requests[1].Context!.Sections, s => s.Id == ContextSectionId.Profile);
        Assert.DoesNotContain("LIVE_PROFILE_SENTINEL", string.Join("\n", requests[1].Messages.Select(m => m.Text)));
        var priorTool = Assert.Single(requests[1].Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AgentStateInspect);
        using var tool = JsonDocument.Parse(priorTool.Text);
        var fact = Assert.Single(requests[1].Context!.Sections, s => s.Id == ContextSectionId.ExecutionFacts);
        Assert.Equal(ContextLayer.Runtime, fact.Layer); Assert.Equal(ContextSemantics.TrustedFact, fact.Semantics);
        Assert.Contains("\"ProfileEvidence\":false", fact.Message.Text);
        Assert.Contains("openPages", priorTool.Text); Assert.Contains("openPages", fact.Message.Text);
        const string observed = "Trusted runtime observation: "; var at = fact.Message.Text.IndexOf(observed, StringComparison.Ordinal); Assert.True(at >= 0);
        using var latestFact = JsonDocument.Parse(fact.Message.Text[(at + observed.Length)..]);
        var priorPages = tool.RootElement.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "browser").GetProperty("openPages").GetInt32();
        var newPages = latestFact.RootElement.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "browser").GetProperty("openPages").GetInt32();
        Assert.True(newPages > priorPages);
        Assert.All(requests, r => { Assert.Single(r.Context!.Sections, s => s.Id == ContextSectionId.ExecutionFacts); Assert.Single(r.Messages, m => m.Role == ModelRole.User && m.Text == "Observe the currently owned Profile"); });
        Assert.Equal("LIVE_PROFILE_SENTINEL", (await Json(await owner.GetAsync(api + "/me/profile"))).GetProperty("profile").GetProperty("preferences").GetProperty("preferredName").GetProperty("value").GetString());
    }

    [Theory(Timeout = 90000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Run_Profile_source_change_or_read_policy_loss_during_model_output_never_commits_the_new_answer(bool sqlite, bool revoke)
    {
        var model = new ProfileProbeModel { Hold = true, Answer = "PROFILE_DERIVED_UNAUTHORIZED_OUTPUT" }; await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        await SetOwnProfile(owner, tenant, "Captured private Profile"); await ProfileExecutor(owner, tenant, instance);
        var submitted = await SubmitProfileRequest(owner, tenant, instance); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, submitted.Run))!;
            Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(active.Checkpoint).TenantProfileSource);
            if (revoke) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/memory.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            else await SetOwnProfile(owner, tenant, "Changed private Profile");
            model.Release.TrySetResult(); var terminal = await ProfileTerminal(factory, tenant, submitted.Run); Assert.NotEqual(AgentRunStatus.Completed, terminal.Status);
            var history = await Json(await owner.GetAsync(api + $"/sessions/{submitted.Session}/messages?limit=100")); Assert.DoesNotContain("PROFILE_DERIVED_UNAUTHORIZED_OUTPUT", history.GetRawText());
            Assert.Single(model.Requests);
        }
        finally { model.Release.TrySetResult(); }
    }

    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Run_Profile_admitted_exclusion_cannot_be_relaxed_until_a_new_independent_Run(bool sqlite)
    {
        var model = new ProfileProbeModel(true) { Hold = true }; await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        await SetOwnProfile(owner, tenant, "PINNED_EXCLUSION_SENTINEL"); await ProfileExecutor(owner, tenant, instance);
        async Task Set(bool enabled, long revision) => Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision, set = new { profileEvidence = enabled }, clear = Array.Empty<string>() })).StatusCode);
        await Set(false, 1); var first = await SubmitProfileRequest(owner, tenant, instance, 2); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await Set(true, 2); model.Release.TrySetResult(); Assert.Equal(AgentRunStatus.Completed, (await ProfileTerminal(factory, tenant, first.Run)).Status);
            Assert.Equal(2, model.Requests.Count); Assert.All(model.Requests, r => Assert.DoesNotContain(r.Context!.Sections, s => s.Id == ContextSectionId.Profile));
            var next = await SubmitProfileRequest(owner, tenant, instance, 3); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            Assert.Equal(AgentRunStatus.Completed, (await ProfileTerminal(factory, tenant, next.Run)).Status);
            Assert.Contains(model.Requests.Skip(2), r => r.Context!.Sections.Any(s => s.Id == ContextSectionId.Profile && s.Message.Text.Contains("PINNED_EXCLUSION_SENTINEL", StringComparison.Ordinal)));
        }
        finally { model.Release.TrySetResult(); }
    }

    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Run_Profile_changes_requester_in_a_shared_conversation_without_borrowing_creator_or_other_tenant_preferences(bool sqlite)
    {
        var model = new ProfileProbeModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        await SetOwnProfile(owner, tenant, "ALPHA_OWNER_PRIVATE"); await ProfileExecutor(owner, tenant, instance);
        var first = await SubmitProfileRequest(owner, tenant, instance); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await ProfileTerminal(factory, tenant, first.Run);
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Shared Chat requester", permissions = new[] { "sessions.create", "sessions.read", "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync(api + "/members/invite", new { login = "profile-requester@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Csrf(member);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "profile-requester@example.test", displayName = "Second requester", password = "strong-profile-requester-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(member, "profile-requester@example.test", "strong-profile-requester-password"); await SetOwnProfile(member, tenant, "ALPHA_MEMBER_PRIVATE");
        var second = await SubmitProfileRequest(member, tenant, instance, existing: first.Session); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var terminal = await ProfileTerminal(factory, tenant, second.Run); Assert.Equal(AgentRunStatus.Completed, terminal.Status);
        var profile = Assert.Single(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Profile);
        Assert.Contains("ALPHA_MEMBER_PRIVATE", profile.Message.Text); Assert.DoesNotContain("ALPHA_OWNER_PRIVATE", profile.Message.Text);
        var memberPrincipal = (await Json(await member.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        Assert.Equal(memberPrincipal, AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).TenantProfileSource!.PrincipalId);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Profile Beta", slug = "profile-beta" }))).GetProperty("tenantId").GetGuid();
        var betaAgent = (await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, beta) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        await SetOwnProfile(owner, beta, "BETA_ACCOUNT_PRIVATE"); await ProfileExecutor(owner, beta, betaAgent);
        var third = await SubmitProfileRequest(owner, beta, betaAgent); await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await ProfileTerminal(factory, beta, third.Run);
        var betaProfile = Assert.Single(model.Requests.Last().Context!.Sections, s => s.Id == ContextSectionId.Profile);
        Assert.Contains("BETA_ACCOUNT_PRIVATE", betaProfile.Message.Text); Assert.DoesNotContain("ALPHA_OWNER_PRIVATE", betaProfile.Message.Text); Assert.DoesNotContain("ALPHA_MEMBER_PRIVATE", betaProfile.Message.Text);
    }
}
