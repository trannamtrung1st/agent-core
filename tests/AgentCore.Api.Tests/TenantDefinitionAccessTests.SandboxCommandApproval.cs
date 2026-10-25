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
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task Tenant_Sandbox_command_approval_and_human_controls_HTTP_bind_exact_witnesses_without_SDK_dispatch(bool stopProcess, bool switchProfile)
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
        string? alternateId = null;
        if (switchProfile)
        {
            var alternate = await Json(await owner.PostAsJsonAsync(api + "/sandbox-profiles", new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Selected source", "Controlled alternate setup", true,
                "ghcr.io/acme/alternate@sha256:" + new string('b', 64))));
            alternateId = alternate.GetProperty("profile").GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sandbox-profiles/{alternateId}/verify", new TenantSandboxVerificationRequest(await Revision(owner, tenant), 1))).StatusCode);
        }
        var definitions = factory.Services.GetRequiredService<TenantDefinitionService>();
        var draft = await definitions.CreateAsync(actor, tenant, "Exact Sandbox", await Revision(owner, tenant));
        draft = await definitions.UpdateAsync(actor, tenant, draft.DraftId, draft.Candidate with { SandboxPolicy = new(profileId, alternateId is null ? [profileId] : [profileId, alternateId]),
            ExecutionPolicy = new(Capabilities: new("Selected", [ToolCatalog.SandboxExec])) }, await Revision(owner, tenant), draft.Revision);
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
        var storage = factory.Services.GetRequiredService<ITenantSandboxRuntimeStore>(); var pinned = Assert.Single(run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!, p => p.Id == profileId);
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
        if (switchProfile)
        {
            var selectionPath = api + $"/sessions/{id}/sandbox"; var selectionControls = await Json(await owner.GetAsync(selectionPath + "/controls"));
            var selectionObserved = selectionControls.GetProperty("observation"); var selectedLease = selectionObserved.GetProperty("environment");
            var target = Assert.Single(selectionControls.GetProperty("profiles").EnumerateArray(), p => p.GetProperty("id").GetString() == alternateId);
            Assert.True(target.GetProperty("canSelect").GetBoolean()); Assert.True(selectionControls.GetProperty("canReset").GetBoolean());
            var change = new TenantSandboxProfileSwitchRequest(selectionObserved.GetProperty("securityRevision").GetInt64(), selectionObserved.GetProperty("sessionRevision").GetInt64(),
                selectedLease.GetProperty("generation").GetGuid(), selectedLease.GetProperty("revision").GetInt64(), null, alternateId!, target.GetProperty("revision").GetInt64(), selectionControls.GetProperty("instanceRevision").GetInt64());
            var switchPath = selectionPath + "/profile";
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(switchPath, change with { ExpectedProfileRevision = 99 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(switchPath, new { change.ExpectedRevision, change.ExpectedSessionRevision, change.ExpectedGeneration, change.ExpectedLeaseRevision, change.ProfileId, change.ExpectedProfileRevision, change.ExpectedInstanceRevision })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(switchPath, new { change.ExpectedRevision, change.ExpectedSessionRevision, change.ExpectedGeneration, change.ExpectedLeaseRevision, expectedExecutionId = (Guid?)null, change.ProfileId, change.ExpectedProfileRevision, change.ExpectedInstanceRevision, command = "must not execute" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(switchPath, change)).StatusCode);
            using var selectionLegacy = RetiredOwnerHeader.CreateClient(factory);
            Assert.Equal(HttpStatusCode.Unauthorized, (await selectionLegacy.PostAsJsonAsync(switchPath, change)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/sessions/{id}/sandbox/profile", change)).StatusCode);
            var csrf = owner.DefaultRequestHeaders.GetValues(AgentCore.Api.Security.IdentityHttp.CsrfHeader).ToArray(); owner.DefaultRequestHeaders.Remove(AgentCore.Api.Security.IdentityHttp.CsrfHeader);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(switchPath, change)).StatusCode); owner.DefaultRequestHeaders.Add(AgentCore.Api.Security.IdentityHttp.CsrfHeader, csrf);
            var selectionAccepted = await owner.PostAsJsonAsync(switchPath, change); Assert.Equal(HttpStatusCode.Accepted, selectionAccepted.StatusCode); Assert.True(selectionAccepted.Headers.CacheControl?.NoStore);
            Assert.Equal(selectionPath, selectionAccepted.Headers.Location?.OriginalString); Assert.False((await Json(selectionAccepted)).GetProperty("retirementConfirmed").GetBoolean());
            var selectionPending = await Json(await owner.GetAsync(selectionPath)); var environment = selectionPending.GetProperty("environment");
            Assert.Equal("Terminating", environment.GetProperty("storedState").GetString()); Assert.Equal(profileId, environment.GetProperty("profileId").GetString());
            Assert.Equal(alternateId, environment.GetProperty("selectedProfileId").GetString()); Assert.Equal("retirement", environment.GetProperty("selection").GetProperty("storedPhase").GetString());
            Assert.False(environment.GetProperty("selectionRestricted").GetBoolean()); Assert.DoesNotContain("hostScope", selectionPending.GetRawText());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(switchPath, change)).StatusCode);
            Assert.Null((await storage.ReadTenantSandboxRuntimeAsync(tenant, id))!.Execution);
            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "sandbox.profile.switch"); return;
        }
        if (stopProcess)
        {
            Assert.Equal(1, await approvals.DispatchPendingAsync());
            run = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!; at = DateTimeOffset.UtcNow;
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
            frame = new(run.Owner, id, run.AgentRunId, run.Claim!.Generation);
            var execution = (await factory.Services.GetRequiredService<TenantSandboxApprovedOperationService>().ReserveAsync(frame, request.RequestId)).Execution;
            var stopControls = await Json(await owner.GetAsync(api + $"/sessions/{id}/sandbox/controls")); var stopObserved = stopControls.GetProperty("observation");
            Assert.True(stopControls.GetProperty("canStop").GetBoolean()); var stopLease = stopObserved.GetProperty("environment");
            var stop = new TenantSandboxProcessStopRequest(stopObserved.GetProperty("securityRevision").GetInt64(), stopObserved.GetProperty("sessionRevision").GetInt64(),
                stopLease.GetProperty("generation").GetGuid(), stopLease.GetProperty("revision").GetInt64(), stopObserved.GetProperty("execution").GetProperty("revision").GetInt64());
            var stopPath = api + $"/sessions/{id}/sandbox/executions/{execution.Id}/stop";
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(stopPath, stop with { ExpectedExecutionRevision = 99 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(stopPath, new { stop.ExpectedRevision, stop.ExpectedSessionRevision, stop.ExpectedGeneration, stop.ExpectedLeaseRevision, stop.ExpectedExecutionRevision, providerId = "forged" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(stopPath, stop)).StatusCode);
            using var oldOwner = RetiredOwnerHeader.CreateClient(factory);
            Assert.Equal(HttpStatusCode.Unauthorized, (await oldOwner.PostAsJsonAsync(stopPath, stop)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/sessions/{id}/sandbox/executions/{execution.Id}/stop", stop)).StatusCode);
            var csrf = owner.DefaultRequestHeaders.GetValues(AgentCore.Api.Security.IdentityHttp.CsrfHeader).ToArray();
            owner.DefaultRequestHeaders.Remove(AgentCore.Api.Security.IdentityHttp.CsrfHeader);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(stopPath, stop)).StatusCode);
            owner.DefaultRequestHeaders.Add(AgentCore.Api.Security.IdentityHttp.CsrfHeader, csrf);
            var acceptedStop = await owner.PostAsJsonAsync(stopPath, stop); Assert.Equal(HttpStatusCode.Accepted, acceptedStop.StatusCode); Assert.True(acceptedStop.Headers.CacheControl?.NoStore);
            Assert.Equal(api + $"/sessions/{id}/sandbox/executions/{execution.Id}", acceptedStop.Headers.Location?.OriginalString);
            var pendingStop = await Json(acceptedStop); Assert.False(pendingStop.GetProperty("terminationConfirmed").GetBoolean());
            var stoppedView = await Json(await owner.GetAsync(acceptedStop.Headers.Location));
            Assert.True(stoppedView.GetProperty("execution").GetProperty("stopRequested").GetBoolean());
            Assert.False(stoppedView.GetProperty("execution").GetProperty("stopDispatchStarted").GetBoolean());
            Assert.True(stoppedView.GetProperty("environment").GetProperty("cleanupPending").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(stopPath, stop)).StatusCode);
            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "sandbox.process.stop.request");
            return;
        }

        var sandboxPath = api + $"/sessions/{id}/sandbox"; var resetControls = await Json(await owner.GetAsync(sandboxPath + "/controls")); var observed = resetControls.GetProperty("observation");
        Assert.True(resetControls.GetProperty("canReset").GetBoolean()); Assert.False(resetControls.GetProperty("canStop").GetBoolean()); var resetLease = observed.GetProperty("environment");
        var reset = new TenantSandboxResetRequest(observed.GetProperty("securityRevision").GetInt64(), observed.GetProperty("sessionRevision").GetInt64(),
            resetLease.GetProperty("generation").GetGuid(), resetLease.GetProperty("revision").GetInt64(), null);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(sandboxPath + "/reset", reset with { ExpectedExecutionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(sandboxPath + "/reset", new { reset.ExpectedRevision, reset.ExpectedSessionRevision, reset.ExpectedGeneration, reset.ExpectedLeaseRevision })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(sandboxPath + "/reset", new { reset.ExpectedRevision, reset.ExpectedSessionRevision, reset.ExpectedGeneration, reset.ExpectedLeaseRevision, expectedExecutionId = (Guid?)null, command = "must not execute" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(sandboxPath + "/reset", reset)).StatusCode);
        using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.PostAsJsonAsync(sandboxPath + "/reset", reset)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/sessions/{id}/sandbox/reset", reset)).StatusCode);
        var accepted = await owner.PostAsJsonAsync(sandboxPath + "/reset", reset); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(sandboxPath, accepted.Headers.Location?.OriginalString); Assert.True(accepted.Headers.CacheControl?.NoStore);
        var receipt = await Json(accepted); Assert.False(receipt.GetProperty("retirementConfirmed").GetBoolean());
        Assert.Equal(3, receipt.GetProperty("leaseRevision").GetInt64()); Assert.Equal(reset.ExpectedRevision + 1, receipt.GetProperty("securityRevision").GetInt64());
        var pending = await Json(await owner.GetAsync(sandboxPath)); Assert.Equal("Terminating", pending.GetProperty("environment").GetProperty("storedState").GetString());
        Assert.True(pending.GetProperty("environment").GetProperty("cleanupPending").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(sandboxPath + "/reset", reset)).StatusCode);
        Assert.Null((await storage.ReadTenantSandboxRuntimeAsync(tenant, id))!.Execution);
        Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "sandbox.reset.request");

    }
}
