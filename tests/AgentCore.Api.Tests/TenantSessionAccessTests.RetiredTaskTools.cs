using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class RetiredTaskToolModel(string retiredTool) : TenantBackgroundModel
    {
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            Assert.Contains(request.Tools ?? [], t => t.Name == "tasks.delegate");
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool))
            {
                yield return new ModelToolCallEvent(new("retired-child-contract", retiredTool, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Assert.DoesNotContain(request.Tools ?? [], t => t.Name.StartsWith("background.", StringComparison.Ordinal));
            yield return new ModelSemanticResponseReady(new("The retired child operation was refused.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    [Theory(Timeout = 90000)]
    [InlineData(false, "background.start")] [InlineData(true, "background.start")]
    [InlineData(false, "background.list")] [InlineData(true, "background.list")]
    [InlineData(false, "background.inspect")] [InlineData(true, "background.inspect")]
    [InlineData(false, "background.take")] [InlineData(true, "background.take")]
    [InlineData(false, "background.acknowledge")] [InlineData(true, "background.acknowledge")]
    public async Task Tenant_actual_model_retired_child_call_has_no_alias_Task_or_worker_graph(bool sqlite, string retiredTool)
    {
        var root = Path.Combine(Path.GetTempPath(), "retired-task-contract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new RetiredTaskToolModel(retiredTool);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(factory, owner);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun? run;
            do { run = await runs.FindTenantRunAsync(setup.Tenant, setup.Source, timeout.Token); if (run?.IsTerminal == true) break; await Task.Delay(25, timeout.Token); } while (true);
            Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status} {run.Failure?.Code}: {run.Failure?.Summary}");
            Assert.Contains(model.Requests, request => request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == retiredTool));
            var sessions = await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(setup.Tenant, null, 0, 100);
            Assert.Single(sessions.Items);
            var tasks = await owner.GetAsync($"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}/tasks");
            tasks.EnsureSuccessStatusCode(); Assert.Empty((await Json(tasks)).GetProperty("items").EnumerateArray());
            Assert.Single(await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
