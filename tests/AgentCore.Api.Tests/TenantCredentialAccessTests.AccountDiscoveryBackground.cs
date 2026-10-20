using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory] [InlineData(false, "email", false)] [InlineData(true, "email", false)] [InlineData(false, "drive", false)] [InlineData(true, "drive", false)]
    [InlineData(false, "email", true)] [InlineData(true, "email", true)] [InlineData(false, "drive", true)] [InlineData(true, "drive", true)]
    public async Task Account_discovery_background_uses_the_same_exact_read_owner_and_child_output_fence(bool sqlite, string family, bool review)
    {
        var childModel = new AccountDiscoveryModel(TenantCredentialDiscoveryContract.Tool(family)!, review);
        var model = new AccountActionBackgroundModel("Discover one exact account metadata page in the child task", childModel);
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, family == "email" ? "EmailAccount" : "GoogleDriveAccount", tools: true);
        var api = $"/api/v2/tenants/{s.Tenant}"; var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        var role = await access.SaveRoleAsync(actor, s.Tenant, null, "Exact background discovery", ["sessions.create", "agent-runs.create", "sessions.read", "credentials.read", family + ".read"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + family + ".read", new { mode = "always", independentReview = false,
            expectedRevision = await Revision(owner, s.Tenant), expectedPolicyRevision = 0 })).StatusCode);
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Start exact account background test",
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
        var parent = await AwaitCredentialRun(runs, s.Tenant, initial, false); Assert.Equal(AgentRunStatus.Completed, parent.Status);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); TenantSession? child = null;
        while (child is null) {
            child = (await sessions.ListTenantSessionsAsync(s.Tenant, null, 0, 100, deadline.Token)).Items.SingleOrDefault(c => c.Snapshot.Origin.OriginatingAgentRunId == initial);
            if (child is null) await Task.Delay(25, deadline.Token);
        }
        var childId = child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value;
        if (review)
        {
            var waiting = await AwaitAccountConsent(runs, s.Tenant, childId, new HashSet<Guid>());
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(s.Tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(child.Owner.SessionId, request.SessionId); Assert.Equal(TenantOperationKind.CredentialRead, request.Operation.Kind);
            Assert.Equal(family, TenantCredentialDiscoveryContract.Parse(request.Operation.CredentialRead!.ArgumentsJson).Family);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision }); Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }
        var finished = await AwaitCredentialRun(runs, s.Tenant, childId, false);
        Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}; {finished.Result?.Text}");
        Assert.Equal(AgentRunOutputContract.BackgroundOutcome, finished.Admission.OutputContract); Assert.Equal(child.Owner.SessionId, finished.SessionId);
        Assert.Contains("Exact connected account", finished.Result!.Text); Assert.Contains("Separate authorization required", finished.Result.Text);
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }
}
