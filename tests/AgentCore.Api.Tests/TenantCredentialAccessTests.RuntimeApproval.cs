using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Execution;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Security;
using AgentCore.Domain.Credentials;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "approve")] [InlineData(true, "approve")]
    [InlineData(false, "uncertain")] [InlineData(true, "uncertain")]
    public async Task Credential_approval_HTTP_projects_exact_consent_and_distinguishes_completed_and_uncertain_effects(bool sqlite, string choice)
    {
        await using var factory = new OAuthFactory(sqlite: sqlite, checkOverride: choice == "uncertain" ? new UncertainRuntimeVerifier() : null); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var created = await Json(await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("Bound Drive", "Token", "GoogleDriveAccount", await Revision(owner, tenant))));
        var credentialId = created.GetProperty("credential").GetProperty("credentialId").GetGuid();
        var start = await owner.PostAsJsonAsync(api + $"/credentials/{credentialId}/oauth/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        var url = (await Json(start)).GetProperty("authorizationUrl").GetString()!;
        using var consent = Client(factory); consent.DefaultRequestHeaders.Add("Cookie", Assert.Single(start.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.SeeOther, (await consent.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = Uri.UnescapeDataString(url.Split("?state=")[1]), ["decision"] = "authorize" }))).StatusCode);
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) })))
            .GetProperty("instance").GetProperty("instanceId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/credential-bindings",
            new TenantBindCredentialRequest(credentialId, "work-drive", await Revision(owner, tenant), 1, 2))).StatusCode);
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var role = await access.SaveRoleAsync(principal, tenant, null, "Bound metadata", ["credentials.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(principal, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "credential", credentialId.ToString("D")), await Revision(owner, tenant));
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
        var runId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Check the bound Drive connection", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var run = (await runs.FindTenantRunAsync(tenant, runId))!;
        var time = factory.Services.GetRequiredService<TimeProvider>(); var ids = factory.Services.GetRequiredService<IIdGenerator>(); var at = time.GetUtcNow();
        run = await factory.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(run.Owner, runId,
            new AgentRunCommand.Claim(run.Revision, at, ids.NewId(), at.AddMinutes(5)), ids.NewId());
        var prepared = await factory.Services.GetRequiredService<TenantApprovalService>().PrepareCredentialCheckAsync(new(tenant, runId), "work-drive", run.Revision, run.Claim!.Generation);
        var request = prepared.Request!; var route = api + $"/approvals/{request.RequestId}";
        var response = await owner.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var detail = await Json(response); Safe(detail);
        Assert.Equal("Check credential connection", detail.GetProperty("summary").GetString()); Assert.Equal("credentials.use", detail.GetProperty("permission").GetString());
        Assert.Equal("credential", detail.GetProperty("targetResource").GetProperty("type").GetString());
        var action = detail.GetProperty("preparedAction"); Assert.Equal("check_credential_connection", action.GetProperty("operation").GetString());
        Assert.Equal(credentialId, action.GetProperty("credentialCheck").GetProperty("credentialId").GetGuid());
        Assert.Equal("work-drive", action.GetProperty("credentialCheck").GetProperty("reference").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, action.GetProperty("persona").ValueKind); Assert.Equal(System.Text.Json.JsonValueKind.Null, action.GetProperty("preferences").ValueKind);
        Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
        Assert.Single((await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("items").EnumerateArray());
        var decided = await owner.PostAsJsonAsync(route + "/decision", new { decision = choice == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
            expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode); Assert.Equal(choice == "reject" ? "rejected" : "approved", (await Json(decided)).GetProperty("status").GetString());
        if (choice == "reject") Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId));
        else
        {
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            var queued = (await runs.FindTenantRunAsync(tenant, runId))!; at = time.GetUtcNow();
            run = await factory.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(queued.Owner, runId,
                new AgentRunCommand.Claim(queued.Revision, at, ids.NewId(), at.AddMinutes(5)), ids.NewId());
            var service = factory.Services.GetRequiredService<TenantCredentialCheckService>();
            var result = await service.ExecuteAsync(new(tenant, runId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash);
            Assert.Equal(choice == "uncertain" ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, result.Outcome);
            Assert.Equal(result, await service.ExecuteAsync(new(tenant, runId), request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash));
            var projected = await Json(await owner.GetAsync(route)); Safe(projected);
            Assert.Equal(choice == "uncertain" ? "indeterminate" : "completed", projected.GetProperty("operationResult").GetProperty("outcome").GetString());
            Assert.Equal(choice == "uncertain" ? "credential_check_uncertain" : "credential_connection_checked", projected.GetProperty("operationResult").GetProperty("reasonCode").GetString());
            var grant = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId))!;
            Assert.Equal(choice == "uncertain" ? OperationGrantStatus.Indeterminate : OperationGrantStatus.Consumed, grant.Status);
            Assert.False(projected.GetProperty("review").GetProperty("canDecide").GetBoolean());
            var audit = await Json(await owner.GetAsync(api + "/audit?limit=100")); Safe(audit);
            Assert.Contains(audit.GetProperty("items").EnumerateArray(), entry => entry.GetProperty("action").GetString() == TenantCredentialCheckRules.Action
                && entry.GetProperty("decision").GetString() == (choice == "uncertain" ? "indeterminate" : "allowed"));
        }
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    private sealed class UncertainRuntimeVerifier : ITenantCredentialOAuthVerifier
    {
        public CredentialOAuthDescriptor Describe(CredentialPurpose purpose) => new("synthetic", "https://localhost/callback", true, true);
        public ValueTask<CredentialOAuthCheck> CheckAsync(Credential current, CancellationToken ct) => ValueTask.FromException<CredentialOAuthCheck>(new IOException("private-provider-body"));
    }
}
