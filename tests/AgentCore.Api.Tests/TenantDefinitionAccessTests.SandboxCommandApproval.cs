using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Tenant_Sandbox_command_approval_HTTP_projects_exact_review_and_decision_without_claiming_execution()
    {
        await using var factory = new SandboxSetupFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var profile = await Json(await owner.PostAsJsonAsync(api + "/sandbox-profiles", new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Approved source", "Controlled setup", true,
            "ghcr.io/acme/tool@sha256:" + new string('a', 64))));
        var profileId = profile.GetProperty("profile").GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sandbox-profiles/{profileId}/verify", new TenantSandboxVerificationRequest(await Revision(owner, tenant), 1))).StatusCode);
        var definitions = factory.Services.GetRequiredService<TenantDefinitionService>();
        var draft = await definitions.CreateAsync(actor, tenant, "Exact Sandbox", await Revision(owner, tenant));
        draft = await definitions.UpdateAsync(actor, tenant, draft.DraftId, draft.Candidate with { SandboxPolicy = new(profileId, [profileId]),
            Environment = new(Capabilities: new("Selected", [ToolCatalog.SandboxExec])) }, await Revision(owner, tenant), draft.Revision);
        await definitions.PublishAsync(actor, tenant, draft.DraftId, await Revision(owner, tenant), draft.Revision);
        var created = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = Guid.Parse(draft.DefinitionId), version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = created.GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(actor, tenant, null, "Sandbox command", ["sandbox.execute"], await Revision(owner, tenant));
        await access.AssignRoleAsync(actor, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
        role = await access.SaveRoleAsync(actor, tenant, null, "Sandbox source", ["sandbox.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(actor, tenant, executor, role.RoleId, TenantSandboxProfileRules.Scope(tenant, profileId), await Revision(owner, tenant));
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid();
        var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Prepare exact Sandbox command", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var run = (await runs.FindTenantRunAsync(tenant, submitted.GetProperty("runId").GetGuid()))!;
        var at = DateTimeOffset.UtcNow; var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
        var storage = factory.Services.GetRequiredService<ITenantSandboxRuntimeStore>(); var pinned = Assert.Single(run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!);
        var frame = new TenantRuntimeFrame(run.Owner, id, run.AgentRunId, run.Claim!.Generation);
        var lease = new SandboxLease(id, instance, Guid.NewGuid(), 1, pinned, SandboxLeaseState.Provisioning, null, at, at.AddMinutes(10),
            AdmissionRunId: run.AgentRunId, AdmissionToolCallId: "prepare-environment", TenantId: tenant, ExecutorPrincipalId: executor,
            ProviderHostScope: factory.Services.GetRequiredService<ISandboxVerificationHost>().Scope);
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(new(frame, await Revision(owner, tenant), run.Revision), lease, 0, at));
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(tenant, lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var approvals = factory.Services.GetRequiredService<TenantApprovalService>();
        var request = (await approvals.PrepareSandboxAsync(new(tenant, run.AgentRunId), profileId, new("printf exact-http-command", TimeoutSeconds: 30), run.Revision, run.Claim.Generation)).Request!;
        var path = api + $"/approvals/{request.RequestId}"; var response = await owner.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var view = await Json(response); Assert.Equal("review", view.GetProperty("kind").GetString());
        Assert.Equal("run_sandbox_command", view.GetProperty("preparedAction").GetProperty("operation").GetString());
        Assert.Contains("exact-http-command", view.GetProperty("preparedAction").GetProperty("sandboxCommand").GetProperty("commandJson").GetString());
        Assert.True(view.GetProperty("review").GetProperty("canDecide").GetBoolean());
        var decided = await owner.PostAsJsonAsync(path + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
            expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode); var approved = await Json(await owner.GetAsync(path));
        Assert.Equal("approved", approved.GetProperty("status").GetString()); Assert.Equal(System.Text.Json.JsonValueKind.Null, approved.GetProperty("operationResult").ValueKind);
        Assert.Equal("unrecorded", approved.GetProperty("review").GetProperty("executionState").GetString());
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other command review", slug = "other-command-review" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/approvals/{request.RequestId}")).StatusCode);
        Assert.Null((await storage.ReadTenantSandboxRuntimeAsync(tenant, id))!.Execution);
    }
}
