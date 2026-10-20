using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class AccountDiscoveryModel(string tool, bool review) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, true);
        public Func<Task>? BeforeAnswer;
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            var name = review ? ToolCatalog.RequestAuthorization : tool;
            if (request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == name) is not { } result)
            {
                Assert.Contains(request.Tools!, t => t.Name == tool);
                var args = review ? JsonSerializer.Serialize(new { operation = TenantCredentialReadAuthorizationContract.Operation, arguments = new { limit = 1, family = TenantCredentialDiscoveryContract.Family(tool) } }) : "{\"limit\":1}";
                yield return new ModelToolCallEvent(new("account-discovery", name, args)); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var data = JsonDocument.Parse(result.Text); var root = data.RootElement;
            var metadata = review ? root.TryGetProperty("credentialMetadata", out var saved) && saved.ValueKind == JsonValueKind.Object ? saved : default : root;
            var answer = metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("items", out var items)
                ? "Account discovery: " + string.Join("; ", items.EnumerateArray().Select(i => i.GetRawText())) : "Account metadata was not delivered.";
            if (Interlocked.Exchange(ref BeforeAnswer, null) is { } before) await before();
            if (request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true)
            {
                yield return new ModelToolCallEvent(new("account-discovery-finish", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new { outcome = "Response", summary = answer, attentionRequired = false })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new(answer, new(ModelSpeechMode.None, null), [])); yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    public static TheoryData<bool, string, string> AccountDiscoveryCases
    {
        get {
            var cases = new TheoryData<bool, string, string>();
            foreach (var sqlite in new[] { false, true }) foreach (var family in new[] { "email", "drive" })
            foreach (var choice in new[] { "direct", "review", "review-final-family", "review-final-metadata", "reject", "cancel", "stale", "visibility", "no-reviewer", "hidden", "final-family", "final-metadata" }) cases.Add(sqlite, family, choice);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(AccountDiscoveryCases))]
    public async Task Model_account_discovery_filters_independent_family_reads_supports_exact_Review_and_fences_final_output(bool sqlite, string family, string choice)
    {
        var tool = TenantCredentialDiscoveryContract.Tool(family)!; var review = choice.StartsWith("review", StringComparison.Ordinal) || choice is "reject" or "cancel" or "stale" or "visibility" or "no-reviewer";
        var model = new AccountDiscoveryModel(tool, review);
        var diagnostics = new AccountReadDiagnostics();
        await using var source = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        await using var factory = source.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(diagnostics)));
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, family == "email" ? "EmailAccount" : "GoogleDriveAccount", tools: true);
        var api = $"/api/v2/tenants/{s.Tenant}"; var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        async Task<RoleAssignment> Grant(string title, string[] permissions) {
            var role = await access.SaveRoleAsync(actor, s.Tenant, null, title, permissions, await Revision(owner, s.Tenant));
            return await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        }
        await Grant("Read discovery Session", ["sessions.read"]);
        var metadata = await Grant("Read exact metadata", ["credentials.read"]);
        var familyRead = choice == "hidden" ? null : await Grant("Read exact account family", [family + ".read"]);
        if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + family + ".read", new { mode = "always", independentReview = choice == "no-reviewer", expectedRevision = await Revision(owner, s.Tenant), expectedPolicyRevision = 0 })).StatusCode);
        if (choice.EndsWith("final-family", StringComparison.Ordinal) || choice.EndsWith("final-metadata", StringComparison.Ordinal)) model.BeforeAnswer = async () => await access.RevokeAssignmentAsync(actor, s.Tenant,
            choice.EndsWith("final-family", StringComparison.Ordinal) ? familyRead!.AssignmentId : metadata.AssignmentId, await Revision(owner, s.Tenant));
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Discover only the exact bound account family",
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); TenantApprovalRequest? request = null;
        if (review && choice != "no-reviewer")
        {
            var waiting = await AwaitAccountConsent(runs, s.Tenant, initial, new HashSet<Guid>());
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
            request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(s.Tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(TenantOperationKind.CredentialRead, request.Operation.Kind); Assert.Equal(ApprovalKind.Review, request.Kind);
            Assert.Equal(family, TenantCredentialDiscoveryContract.Parse(request.Operation.CredentialRead!.ArgumentsJson).Family);
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(s.Tenant, request.RequestId));
            if (choice == "visibility")
            {
                var identity = factory.Services.GetRequiredService<IdentityService>();
                var account = await identity.RegisterAsync("discovery-reader@example.test", "Discovery reader", "strong-discovery-reader-password");
                var readerRole = await access.SaveRoleAsync(actor, s.Tenant, null, "Read exact inventory consent", ["approvals.read", "credentials.read", "sessions.read"], await Revision(owner, s.Tenant));
                var invite = await access.InviteAsync(actor, s.Tenant, account.LoginKey, readerRole.RoleId, await Revision(owner, s.Tenant));
                var member = await access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
                using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-discovery-reader-password");
                var detailRoute = api + $"/approvals/{request.RequestId}";
                Assert.Equal(0, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(detailRoute)).StatusCode);
                var familyRole = await access.SaveRoleAsync(actor, s.Tenant, null, "Read inventory family", [family + ".read"], await Revision(owner, s.Tenant));
                var visible = await access.AssignRoleAsync(actor, s.Tenant, member.PrincipalId, familyRole.RoleId,
                    AccessScope.ForResource(s.Tenant, "credential", s.Credential.ToString("D")), await Revision(owner, s.Tenant));
                Assert.Equal(1, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(detailRoute)).StatusCode);
                await access.RevokeAssignmentAsync(actor, s.Tenant, visible.AssignmentId, await Revision(owner, s.Tenant));
                Assert.Equal(0, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(detailRoute)).StatusCode);
            }
            if (choice == "stale") await access.RevokeAssignmentAsync(actor, s.Tenant, familyRead!.AssignmentId, await Revision(owner, s.Tenant));
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal(choice != "stale", detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.True(detail.GetProperty("review").GetProperty("canCancel").GetBoolean());
            using var decision = choice is "cancel" or "stale" or "visibility" ? await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })
                : await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = choice == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }
        AgentRun finished;
        try { finished = await AwaitCredentialRun(runs, s.Tenant, initial, false); }
        catch (OperationCanceledException) {
            var current = (await runs.FindTenantRunAsync(s.Tenant, initial))!;
            throw new Xunit.Sdk.XunitException($"{current.Status}; {current.Failure?.Code}; calls={model.Requests.Count}; logs={string.Join("\n", diagnostics.Failures.TakeLast(5))}");
        }
        if (choice.EndsWith("final-family", StringComparison.Ordinal) || choice.EndsWith("final-metadata", StringComparison.Ordinal)) {
            Assert.Equal(AgentRunStatus.Failed, finished.Status); Assert.Equal("protected-output-unavailable", finished.Failure!.Code); Assert.Null(finished.Result);
            Assert.DoesNotContain((await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(s.Tenant, session))!.Snapshot.Entries,
                e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed && e.Text.Contains("Exact connected account", StringComparison.Ordinal));
        }
        else {
            Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}; {finished.Failure?.Summary}");
            if (choice is "direct" or "review") {
                Assert.Contains("Exact connected account", finished.Result!.Text); Assert.Contains("authEpoch", finished.Result.Text);
                Assert.Contains("Separate authorization required", finished.Result.Text); Assert.DoesNotContain("ProtectedPayload", finished.Result.Text);
            }
            else Assert.DoesNotContain("Exact connected account", finished.Result!.Text);
            if (choice == "hidden") Assert.Contains("Account discovery:", finished.Result!.Text);
        }
        Assert.Equal(0, source.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }
    [Theory] [InlineData(false, "email", false)] [InlineData(true, "email", false)] [InlineData(false, "drive", false)] [InlineData(true, "drive", false)]
    [InlineData(false, "email", true)] [InlineData(true, "email", true)] [InlineData(false, "drive", true)] [InlineData(true, "drive", true)]
    public async Task Account_discovery_paging_filters_hidden_aliases_and_final_save_rechecks_the_saved_lookahead(bool sqlite, string family, bool revoke)
    {
        var model = new AccountDiscoveryModel(TenantCredentialDiscoveryContract.Tool(family)!, false);
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, family == "email" ? "EmailAccount" : "GoogleDriveAccount", tools: true);
        var api = $"/api/v2/tenants/{s.Tenant}"; var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        async Task<Guid> Bind(string alias, string label, int instanceRevision) {
            var c = (await Json(await owner.PostAsJsonAsync(api + "/credentials", new { displayName = label, kind = "Token", purpose = family == "email" ? "EmailAccount" : "GoogleDriveAccount",
                expectedRevision = await Revision(owner, s.Tenant), emailAccount = family == "email" ? new { preset = "Gmail", emailAddress = "" } : null }))).GetProperty("credential").GetProperty("credentialId").GetGuid();
            using var bound = await owner.PostAsJsonAsync(api + $"/agents/{s.Instance}/credential-bindings", new { credentialId = c, reference = alias,
                expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = instanceRevision, expectedCredentialRevision = 1 }); Assert.Equal(HttpStatusCode.Created, bound.StatusCode); return c;
        }
        var later = await Bind("later-account", "Later unread undisclosed name", 2);
        var hidden = await Bind("aaa-hidden", "Hidden account must not affect pages", 3);
        var basic = await access.SaveRoleAsync(actor, s.Tenant, null, "Page metadata and Session", ["sessions.read", "credentials.read"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, basic.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        var role = await access.SaveRoleAsync(actor, s.Tenant, null, "Read exact paged family", [family + ".read"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, TenantCredentialRules.Scope(s.Tenant, s.Credential), await Revision(owner, s.Tenant));
        var lookahead = await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, TenantCredentialRules.Scope(s.Tenant, later), await Revision(owner, s.Tenant));
        if (revoke) model.BeforeAnswer = async () => await access.RevokeAssignmentAsync(actor, s.Tenant, lookahead.AssignmentId, await Revision(owner, s.Tenant));
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = 4 }))).GetProperty("sessionId").GetGuid();
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the filtered first account page",
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var finished = await AwaitCredentialRun(factory.Services.GetRequiredService<ITenantRunStore>(), s.Tenant, initial, false);
        Assert.Equal(revoke ? AgentRunStatus.Failed : AgentRunStatus.Completed, finished.Status);
        if (revoke) { Assert.Equal("protected-output-unavailable", finished.Failure!.Code); Assert.Null(finished.Result); }
        var text = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == TenantCredentialDiscoveryContract.Tool(family)).Text;
        using var result = JsonDocument.Parse(text); var root = result.RootElement;
        Assert.True(root.GetProperty("hasMore").GetBoolean()); Assert.Equal("exact-account", root.GetProperty("nextCursor").GetString());
        Assert.Equal("exact-account", Assert.Single(root.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
        Assert.Equal(2, root.GetProperty("credentialReads").GetArrayLength()); Assert.DoesNotContain(hidden.ToString("D"), text);
        Assert.DoesNotContain("aaa-hidden", text); Assert.DoesNotContain("Later unread undisclosed name", text);
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

}
