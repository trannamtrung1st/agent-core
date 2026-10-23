using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)]
    [InlineData(false, "scheduled")] [InlineData(true, "scheduled")]
    [InlineData(false, "manual")] [InlineData(true, "manual")]
    public async Task Actual_independent_Automation_checks_the_complete_reviewed_permission_catalog_without_a_fictitious_requester(bool sqlite, string origin)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-policy-defaults-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => false, new TenantRunExecution(
                p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(),
                p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>(), p.GetRequiredService<TenantBackgroundCompletionService>()))));
        }
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            using var owner = factory.CreateClient();
            var setup = await StartTenantBackground(factory, owner, submit: false, fileCapabilities: [ToolCatalog.ExecutionWait], independentTask: true);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var parent = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, setup.Session))!;
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var source = await access.SaveRoleAsync(human, tenant, null, "Retained actual Automation admission", ["automations.run"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, source.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var role = await access.SaveRoleAsync(human, tenant, null, "Explicit catalog audit executor", PermissionCatalog.All.Where(p => !p.HumanOnly && !p.SystemOnly).Select(p => p.Key).ToArray(), await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            // A distinct current human makes the stricter independent-review branch satisfiable.
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var reviewer = await identity.RegisterAsync("policy-reviewer@example.test", "Policy reviewer", "strong-policy-reviewer-password");
            var iamStore = factory.Services.GetRequiredService<IIamStore>();
            var ownerRole = (await iamStore.ReadTenantAsync(tenant))!.Roles.Single(r => r.Key == BuiltinAccessRoles.Owner);
            var invitation = await access.InviteAsync(human, tenant, reviewer.LoginKey, ownerRole.RoleId, await Revision(owner, tenant));
            await access.AcceptInvitationAsync(reviewer.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            var id = await TenantCredentialAccessTests.AdmitIndependentTask(factory, owner, tenant, parent.Owner.AgentInstanceId,
                "Audit current operation decisions without dispatching effects.", clock, origin);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
            var run = (await runs.FindTenantRunAsync(tenant, id))!;
            Assert.Equal(origin == "manual" ? human : (Guid?)null, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            Assert.Equal(AgentRunTaskSource.Automation, run.Admission.TaskIntent!.Source);
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, id, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
            var operations = factory.Services.GetRequiredService<TenantOperationAuthorizer>();
            var reference = new TenantRunReference(tenant, id);
            // No resource selectors means the exact tenant scope from this trusted Run reference.
            // This audits defaults, not the owning-resource/effect matrix.
            using var inventory = JsonDocument.Parse(typeof(TenantSessionAccessTests).Assembly.GetManifestResourceStream("AgentCore.Api.Tests.Fixtures.tenant-permission-policy-inventory.json")!);
            var entries = inventory.RootElement.EnumerateArray().ToArray();
            Assert.Equal(78, entries.Length); Assert.Equal(PermissionCatalog.All.Select(p => p.Key).Order(StringComparer.Ordinal), entries.Select(p => p.GetProperty("permission").GetString()));
            foreach (var entry in entries)
            {
                var key = entry.GetProperty("permission").GetString()!; var metadata = PermissionCatalog.Get(key);
                Assert.Equal(entry.GetProperty("resourceType").GetString(), metadata.ResourceType);
                Assert.Equal(entry.GetProperty("approvalClass").GetString(), metadata.ApprovalClass.ToString());
                Assert.Equal(entry.GetProperty("humanOnly").GetBoolean(), metadata.HumanOnly);
                Assert.Equal(entry.GetProperty("delegatable").GetBoolean(), metadata.Delegatable);
                Assert.Equal(entry.GetProperty("systemOnly").GetBoolean(), metadata.SystemOnly);
                var decision = await operations.CheckAsync(reference, key, null!, null!);
                var denied = entry.GetProperty("humanOnly").GetBoolean() || entry.GetProperty("systemOnly").GetBoolean();
                var review = entry.GetProperty("minimum").GetString() == "Always";
                Assert.True(decision.Decision == (denied ? OperationDecisionKind.Denied : review ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Allowed), $"{origin}: {key}: {decision}");
                Assert.Equal(denied ? "human_only" : review ? "review_required" : "allowed", decision.ReasonCode);
                Assert.Equal(review && !denied ? ApprovalKind.Review : (ApprovalKind?)null, decision.ApprovalKind);
            }
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.update", new { mode = "always", independentReview = true,
                expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var strictRevision = await Revision(owner, tenant);
            Assert.Equal(OperationDecisionKind.RequiresApproval, (await operations.CheckAsync(reference, "agents.update", null!, null!)).Decision);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.update", new { mode = "none", independentReview = false,
                expectedRevision = strictRevision, expectedPolicyRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(api + "/operation-policies/http.request", new { mode = "whenRequesterLacksPermission", independentReview = false,
                expectedRevision = strictRevision, expectedPolicyRevision = 0 })).StatusCode);
            Assert.Equal(strictRevision, await Revision(owner, tenant));
            await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            var exceptional = await operations.CheckAsync(reference, "http.request", null!, null!);
            Assert.Equal(OperationDecisionKind.RequiresApproval, exceptional.Decision); Assert.Equal(ApprovalKind.ExceptionalGrant, exceptional.ApprovalKind);
            var native = await operations.CheckAsync(reference, "browser.use", null!, null!);
            Assert.Equal(OperationDecisionKind.Denied, native.Decision); Assert.Equal("nondelegatable", native.ReasonCode);
            Assert.Equal(run.Revision, (await runs.FindTenantRunAsync(tenant, id))!.Revision);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
