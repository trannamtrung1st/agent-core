using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private const string PrivateSkillKey = "instance:private-procedure";
    private const string PrivateSkillProcedure = "PRIVATE-PROCEDURE-SENTINEL: Compare only the exact saved totals; do not grant any capability.";
    private sealed class PrivateSkillModel(bool always, bool background = false, string key = PrivateSkillKey) : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Func<CancellationToken, Task>? BeforeFirst;
        public ModelCapabilities Capabilities { get; } = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var first = Requests.IsEmpty; Requests.Enqueue(request);
            if (first && BeforeFirst is not null) await BeforeFirst(ct);
            var messages = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            ModelToolCall? next = null;
            var load = messages.LastOrDefault(m => m.Name == ToolCatalog.SkillsLoad);
            var consent = messages.LastOrDefault(m => m.Name == ToolCatalog.RequestAuthorization);
            if (!always && load is null) next = new("load-private", ToolCatalog.SkillsLoad, TenantSkillLoadContract.Arguments([key]));
            else if (!always && consent is null && load!.Text.Contains("\"requiresApproval\"", StringComparison.Ordinal))
                next = new("read-private", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_management", read = new { tool = ToolCatalog.SkillsLoad, arguments = new { ids = new[] { key } } } }));
            else if (!always && consent?.Text.Contains("managementReadObservation", StringComparison.Ordinal) == true && !messages.Any(m => m.ToolCallId == "load-after-consent"))
                next = new("load-after-consent", ToolCatalog.SkillsLoad, TenantSkillLoadContract.Arguments([key]));
            if (next is not null) { yield return new ModelToolCallEvent(next); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            if (background)
            {
                yield return new ModelToolCallEvent(new("private-skill-done", ToolCatalog.WorkComplete,
                    """{"outcome":"Response","summary":"The exact private procedure has its recorded activation outcome.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelTextDelta("The private procedure has its recorded activation outcome. No configuration was changed.");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    public static IEnumerable<object[]> PrivateSkillCases => from sqlite in new[] { false, true }
        from mode in new[] { "direct", "approve", "reject", "no_read", "always", "changed", "revoked", "frozen", "always_no_read", "always_review", "requester", "budget_no_read" }
        select new object[] { sqlite, mode };
    [Theory(Timeout = 120000)] [MemberData(nameof(PrivateSkillCases))]
    public async Task Canonical_private_Skills_load_with_independent_reads_exact_consent_and_current_source_fences_when_management_is_Off(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "private-skill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new PrivateSkillModel(mode.StartsWith("always", StringComparison.Ordinal));
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var logs = new ManagementMutationLogs(); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}"; var path = api + $"/agents/{instance}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = mode == "frozen" ? "Managed" : "Disabled" }, scopes = new { value = mode == "frozen" ? new[] { "Settings" } : Array.Empty<string>() }, autoApply = new { value = Array.Empty<string>() } } })).StatusCode);
            async Task Write(long revision, string operation, string procedure)
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/skills/commands", new { operation, key = operation == "create" ? null : PrivateSkillKey,
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision, expectedSkillRevision = operation == "create" ? (long?)null : 1,
                    input = new { id = "private-procedure", name = "Private procedure", description = "Private description sentinel", procedure,
                        projection = mode.StartsWith("always", StringComparison.Ordinal) ? "Always" : "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
            }
            await Write(3, "create", PrivateSkillProcedure);
            if (mode == "budget_no_read")
                for (var index = 0; index < 2; index++)
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4 + index,
                        input = new { id = "private-always-" + index, name = "Private Always " + index, description = "Private reserved budget", procedure = new string((char)('a' + index), 4000),
                            projection = "Always", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            var iam = host.Services.GetRequiredService<IIamStore>(); var security = (await iam.ReadTenantAsync(tenant))!; var actor = Assert.Single(security.Users).PrincipalId;
            var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(actor, tenant, null, "One private Skill reader", ["skills.read"], await Revision(owner, tenant));
            if (mode is not ("no_read" or "always_no_read" or "budget_no_read")) await access.AssignRoleAsync(actor, tenant, agent.PrincipalId, role.RoleId,
                AccessScope.ForResource(tenant, "skill", instance.ToString("D") + ":" + PrivateSkillKey), await Revision(owner, tenant));
            if (mode is "approve" or "reject" or "always_review" or "requester") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/skills.read",
                new { mode = mode == "requester" ? "whenRequesterLacksPermission" : "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (mode == "changed") model.BeforeFirst = _ => Write(4, "update", "CHANGED-PROCEDURE-MUST-NOT-REPLACE-THE-PIN");
            if (mode == "revoked") model.BeforeFirst = async ct => { await access.SaveRoleAsync(actor, tenant, role.RoleId, role.Name, ["skills.manage"], await Revision(owner, tenant), ct); };
            if (mode == "frozen") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/self-management/freeze", new {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4 })).StatusCode);
            var expectedInstanceRevision = mode == "frozen" ? 5 : mode == "budget_no_read" ? 6 : 4;
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision }));
            var session = created.GetProperty("sessionId").GetGuid();
            using var requester = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var submitter = owner; Guid? requesterPrincipal = null;
            if (mode == "requester")
            {
                var account = await host.Services.GetRequiredService<IdentityService>().RegisterAsync("private-requester@example.test", "Private procedure requester", "strong-private-requester-password");
                var memberRole = await access.SaveRoleAsync(actor, tenant, null, "Chat without private procedures", ["agents.read", "agents.chat", "sessions.read", "approvals.read"], await Revision(owner, tenant));
                var invitation = await access.InviteAsync(actor, tenant, account.LoginKey, memberRole.RoleId, await Revision(owner, tenant));
                requesterPrincipal = (await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token)).PrincipalId;
                var context = await host.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance);
                var contextRole = await access.SaveRoleAsync(actor, tenant, null, "Requester exact private Skill context", ["spaces.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(actor, tenant, requesterPrincipal.Value, contextRole.RoleId,
                    TenantSpaceService.Scope(tenant, context!.SpaceId), await Revision(owner, tenant));
                await Login(requester, account.LoginKey, "strong-private-requester-password"); submitter = requester;
            }
            var response = await submitter.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Use the exact private-procedure Skill for this task.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var final = await AwaitManagementRun(runs, tenant, runId, true);
            if (mode is "approve" or "reject" or "requester")
            {
                Assert.True(final.Status == AgentRunStatus.WaitingForApproval, $"{final.Status}: {final.Failure}; {string.Join(" | ", logs.Errors)}");
                Assert.Equal(0, final.SkillLoadCount); Assert.DoesNotContain(PrivateSkillKey, final.ActiveSkillKeys);
                Assert.All(model.Requests, r => Assert.DoesNotContain(PrivateSkillProcedure, r.Context!.Text(ContextSectionId.InstanceSkills)));
                var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, final.ApprovalRequestId!.Value))!;
                Assert.Equal(ToolCatalog.SkillsLoad, request.Operation.ManagementRead!.ToolName); Assert.Equal(ApprovalKind.Review, request.Kind);
                if (mode == "requester")
                {
                    Assert.Equal(requesterPrincipal, request.OriginalInitiatorPrincipalId);
                    Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                    Assert.Equal(0, (await Json(await requester.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                }
                var detail = await owner.GetAsync(api + $"/approvals/{request.RequestId}"); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                Assert.Contains(PrivateSkillProcedure, await detail.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "requester" ? "approve" : mode,
                    actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    final = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.AgentRunId != runId && r.ApprovalRequestId == request.RequestId)!;
                    if (final?.IsTerminal == true) break; await Task.Delay(25, deadline.Token);
                }
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            }
            var expectedFailure = mode is "changed" or "revoked";
            Assert.True(expectedFailure ? final.Status != AgentRunStatus.Completed : final.Status == AgentRunStatus.Completed,
                $"{final.Status}: {final.Failure}; {string.Join(" | ", logs.Errors)}");
            var activated = mode is "direct" or "approve" or "always" or "frozen" or "requester";
            var pinnedActive = activated || mode is "always_no_read" or "always_review";
            Assert.True(pinnedActive == final.ActiveSkillKeys.Contains(PrivateSkillKey), $"Expected active={activated}; count={final.SkillLoadCount}; tools={string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text))}; {string.Join(" | ", logs.Errors)}");
            Assert.Equal(activated && mode != "always" ? 1 : 0, final.SkillLoadCount);
            Assert.Equal(activated, model.Requests.Any(r => r.Context!.Text(ContextSectionId.InstanceSkills).Contains(PrivateSkillProcedure, StringComparison.Ordinal)));
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Text.Contains("CHANGED-PROCEDURE-MUST-NOT-REPLACE-THE-PIN", StringComparison.Ordinal));
            if (mode == "budget_no_read") Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool), m => m.Text.Contains("over_budget", StringComparison.Ordinal));
            if (!activated) Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Text.Contains(PrivateSkillProcedure, StringComparison.Ordinal));
            if (mode != "always") Assert.DoesNotContain(PrivateSkillProcedure, model.Requests.First().Context!.Text(ContextSectionId.InstanceSkills));
            Assert.Equal(expectedInstanceRevision, final.Admission.Configuration!.InstanceRevision);
            Assert.Contains(final.PinnedSkillCatalog, s => s.Key == PrivateSkillKey && s.Procedure == PrivateSkillProcedure);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
