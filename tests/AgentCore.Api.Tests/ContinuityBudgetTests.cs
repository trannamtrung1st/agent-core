using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityBudgetTests
{
    [Fact(Timeout = 60000)]
    public async Task Thought_skips_identical_cross_scope_decisions_without_a_maintenance_attempt()
    {
        var model = new CrossScopeModel();
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"continuity-scope-{Guid.NewGuid():N}.db"), model,
            configure: services => {
                var original = services.Last(d => d.ServiceType == typeof(IAgentDefinitionStore));
                services.Remove(original);
                services.AddSingleton<IAgentDefinitionStore>(sp => new UserRetrievalDefinitions((IAgentDefinitionStore)(original.ImplementationInstance
                    ?? original.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!))));
            });
        var s = host.Services;
        var id = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9)).InstanceId;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        var memory = s.GetRequiredService<IStructuredMemoryStore>();
        foreach (var scope in new[] { MemoryScope.User, MemoryScope.IdentityUser })
            await memory.InsertAsync(new(Guid.NewGuid(), session.SessionId, MemoryKind.Decision, MemoryItemStatus.Active,
                "Scrum decision", "Use fictional Scrum checklists for Aurora.", "scrum decision", new("user", [], null, now), now, now,
                scope, scope == MemoryScope.User ? null : id, LocalUserProfile.Id));
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
        var thoughts = s.GetRequiredService<ThoughtRegistrationService>();
        var r = await thoughts.SaveAsync(id, null, 0, true, 3600, "try to consolidate your memory and experience", null, null);
        await thoughts.RunNowAsync(id, r.RegistrationId, r.Revision);
        await ThoughtJourneyTests.Intake(s);
        await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
        var work = Assert.Single(await s.GetRequiredService<IWorkItemStore>().ListAsync(new(id, LocalUserProfile.Id), 100));
        Assert.Equal(WorkItemStatus.Completed, work.Status);
        Assert.Equal("NoAction", ThoughtCompletion.Outcome(work.Result!.Text));
        Assert.False(work.Result.AttentionRequired); Assert.Null(work.Approval);
        Assert.Single(await memory.ListActiveUserAsync(LocalUserProfile.Id));
        Assert.Single(await memory.ListActiveIdentityUserAsync(id, LocalUserProfile.Id));
        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain(work.Checkpoint!.PayloadJson, "\"Name\":\"memory.consolidate\"");
    }

    internal sealed class UserRetrievalDefinitions(IAgentDefinitionStore inner) : IAgentDefinitionStore
    {
        private static AgentDefinition Enable(AgentDefinition d) => d.MemoryPolicy is null ? d
            : d with { MemoryPolicy = d.MemoryPolicy with { UserRetrieval = true } };
        public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => (await inner.ListAsync(ct)).Select(Enable).ToArray();
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) =>
            await inner.GetAsync(id, version, ct) is { } d ? Enable(d) : null;
    }

    private sealed class CrossScopeModel : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            Assert.Contains(request.Messages, m => m.Role == ModelRole.System && m.Text == IdentityMaintenanceService.Guidance);
            Assert.Contains("exact same provenance.scope and Memory kind", IdentityMaintenanceService.Guidance);
            Assert.Contains("one narrow maintenance decision", IdentityMaintenanceService.Guidance);
            if (Requests.Count == 1)
                yield return new ModelToolCallEvent(new("search", ToolCatalog.ContinuitySearch, """{"query":"Scrum decision"}"""));
            else
            {
                using var result = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                var items = result.RootElement.GetProperty("result").EnumerateArray().ToArray();
                Assert.Equal(2, items.Length);
                Assert.Equal(items[0].GetProperty("summary").GetString(), items[1].GetProperty("summary").GetString());
                Assert.Equal(new[] { "IdentityUser", "User" }, items.Select(i => i.GetProperty("provenance").GetProperty("scope").GetString()).Order().ToArray());
                yield return new ModelToolCallEvent(new("finish", ToolCatalog.WorkComplete,
                    """{"summary":"Identical decisions have different scopes; no safe consolidation candidate.","attentionRequired":false,"outcome":"NoAction"}"""));
            }
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Tool_search_preserves_complete_records_and_scope_under_reduced_durable_budget()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"continuity-budget-{Guid.NewGuid():N}.db"));
        var s = host.Services;
        var id = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9)).InstanceId;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var definition = session.Definition;
        definition = definition with { MemoryPolicy = definition.MemoryPolicy! with { UserRetrieval = true } };
        var now = DateTimeOffset.UtcNow;
        foreach (var scope in new[] { MemoryScope.User, MemoryScope.IdentityUser })
            await s.GetRequiredService<IStructuredMemoryStore>().InsertAsync(new(Guid.NewGuid(), session.SessionId,
                MemoryKind.Decision, MemoryItemStatus.Active, "Scrum decision", "Retain the fabricated Scrum decision " + string.Join(" ", Enumerable.Repeat("fabricated checklist", 15)),
                "scrum decision", new("user", [], null, now), now, now, scope,
                scope == MemoryScope.User ? null : id, LocalUserProfile.Id));
        var tools = s.GetRequiredService<SessionToolExecutor>();
        var call = new ModelToolCall("search", ToolCatalog.ContinuitySearch, """{"query":"Scrum decision"}""");
        var admission = new ToolExecutionAdmission(true, TriggerKind.ThoughtActivation, AgentInstanceId: id);
        async Task<string> Search(int budget) => (await tools.ExecuteAsync(definition, Guid.Empty, call, budget, admission: admission)).Text;
        var full = await Search(6000);
        using var all = JsonDocument.Parse(full);
        var items = all.RootElement.GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length); Assert.False(all.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(new[] { "IdentityUser", "User" }, items.Select(i => i.GetProperty("provenance").GetProperty("scope").GetString()).Order().ToArray());
        var budget = Encoding.UTF8.GetByteCount(ContinuityService.Serialize(new {
            trust = ContinuityService.TrustLabel, result = new[] { items[0] }, truncated = true }));
        var partial = await Search(budget);
        Assert.InRange(Encoding.UTF8.GetByteCount(partial), 1, budget);
        using var some = JsonDocument.Parse(partial);
        Assert.True(some.RootElement.GetProperty("truncated").GetBoolean());
        var kept = Assert.Single(some.RootElement.GetProperty("result").EnumerateArray());
        Assert.Equal(items[0].GetRawText(), kept.GetRawText()); // No ID/kind/summary/provenance field or item fragment is dropped.
        using var none = JsonDocument.Parse(await Search(80));
        Assert.Equal("finish_required", none.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("output_limit", none.RootElement.GetRawText());
        using var exact = JsonDocument.Parse(await Search(Encoding.UTF8.GetByteCount(full)));
        Assert.Equal(2, exact.RootElement.GetProperty("result").GetArrayLength());
    }
}
