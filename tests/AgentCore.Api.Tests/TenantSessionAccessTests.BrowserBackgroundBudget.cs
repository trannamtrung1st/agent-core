using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class BackgroundBrowserBudgetModel : TenantBackgroundModel, ILanguageModel
    {
        public new ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Origin(request) != ExecutionOrigin.DelegatedTask)
            { await foreach (var item in base.GenerateAsync(request, ct)) yield return item; yield break; }
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete); Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.BrowserSnapshot);
            var reads = request.Messages.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot);
            yield return new ModelToolCallEvent(reads < 26
                ? new("owned-page-" + reads, ToolCatalog.BrowserSnapshot, "{}")
                : new("finish-owned-browser-budget", ToolCatalog.WorkComplete, """{"summary":"Checked 26 changing owned pages.","attentionRequired":false,"outcome":"Response"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_Browser_actual_dispatch_keeps_32_step_pin_through_26_owned_reads_and_completion(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-background-browser-budget-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new BackgroundBrowserBudgetModel(); var browser = new LifecycleReadBrowser { ChangingReads = true };
            void Configure(IServiceCollection services)
            { ((BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!).Enabled = true; services.Replace(ServiceDescriptor.Singleton<IBrowser>(browser)); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); var setup = await StartTenantBackground(factory, owner, submit: false,
                fileCapabilities: ["tasks.delegate", ToolCatalog.BrowserSnapshot], reportReads: true);
            var api = $"/api/v2/tenants/{setup.Tenant}";
            var parent = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(setup.Tenant, setup.Session))!;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, setup.Tenant, null, "Independent owned background page reads", ["agents.read", "browser.read"], await Revision(owner, setup.Tenant));
            await access.AssignRoleAsync(human, setup.Tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant));
            var current = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(setup.Tenant, parent.Owner.AgentInstanceId))!;
            // Reserve the parent's own work and both bounded Task/report shares;
            // the worker's independent Browser ceiling remains 32 steps / 240 seconds.
            var budget = await owner.PutAsJsonAsync(api + $"/agents/{current.Instance.InstanceId}/execution-budgets", new {
                expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = current.Instance.Revision,
                budgets = new ExecutionBudgetPolicy(InteractiveBrowser: new(88, 660)) });
            Assert.True(budget.IsSuccessStatusCode, await budget.Content.ReadAsStringAsync());
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Start the exact owned Browser read budget check",
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }));
            var (child, report) = await WaitTenantBackground(factory, setup.Tenant, setup.Session, sent.GetProperty("runId").GetGuid(), true);
            Assert.Equal(ExecutionBudgetClass.UnattendedBoundBrowser, child.Admission.ExecutionBudget!.Class); Assert.Equal(32, child.Admission.ExecutionBudget.Profile.MaxSteps);
            Assert.Equal(240, child.Admission.ExecutionBudget.Profile.DurationSeconds);
            Assert.Equal(AgentRunStatus.Completed, child.Status); Assert.Equal(27, child.Checkpoint!.StepCount);
            Assert.InRange(child.Checkpoint.RemainingOverallBudgetMs, 1, 240000);
            Assert.Equal("Checked 26 changing owned pages.", child.Result!.Text); Assert.NotNull(report);
            Assert.Equal(26, browser.Requests.Count); Assert.All(browser.Requests, r => { Assert.IsType<BrowserObserve>(r.Command); Assert.True(r.TenantReadOnly);
                Assert.Equal(child.SessionId, r.SessionId); Assert.Equal(child.Owner.TenantId, r.TenantOwner!.TenantId); Assert.Equal(child.AgentInstanceId, r.TenantOwner.AgentInstanceId); });
            var requests = model.Requests.Where(r => TenantBackgroundModel.Origin(r) == ExecutionOrigin.DelegatedTask).ToArray(); Assert.Equal(27, requests.Length);
            Assert.Equal(26, requests.Last().Messages.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot));
            Assert.DoesNotContain(browser.Requests, r => r.Command is BrowserNavigate);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
