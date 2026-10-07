using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace AgentCore.Api.Tests;

public sealed class LiveRuntimeClosureTests(ITestOutputHelper output)
{
    [LiveIdentityMaintenanceFact(Timeout = 360000)]
    public async Task Real_incremental_maintenance_keeps_scope_qualifiers_and_finishes_with_bounded_evidence()
    {
        var options = new LanguageModelProviderOptions { Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/",
            ApiKey = LiveIdentityMaintenanceTests.Key(), DefaultModel = "deepseek/deepseek-v4.1-flash", Tools = true, StructuredOutput = false };
        using var http = new HttpClient();
        var model = new ObservedModel(new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, options)));
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"p910-live-closure-{Guid.NewGuid():N}.db"), model,
            configure: services => {
                services.RemoveAll<IModelCatalog>(); services.AddSingleton<IModelCatalog>(ModelCatalogFactory.Create("Real", options, null));
                services.RemoveAll<ILanguageModelResolver>(); services.AddSingleton<ILanguageModelResolver>(new Resolver(model));
                var original = services.Last(d => d.ServiceType == typeof(IAgentDefinitionStore)); services.Remove(original);
                services.AddSingleton<IAgentDefinitionStore>(sp => new ContinuityBudgetTests.UserRetrievalDefinitions((IAgentDefinitionStore)(
                    original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!))));
            }, configuration: c => c.AddInMemoryCollection(new Dictionary<string, string?> {
                ["AgentCore:Profile"] = "Real", ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
                ["Providers:LanguageModels:primary-llm:BaseUrl"] = options.BaseUrl, ["Providers:LanguageModels:primary-llm:ApiKey"] = options.ApiKey,
                ["Providers:LanguageModels:primary-llm:DefaultModel"] = options.DefaultModel,
                ["Providers:LanguageModels:primary-llm:Tools"] = "true", ["Providers:LanguageModels:primary-llm:StructuredOutput"] = "false" }));
        var s = host.Services;
        var id = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16)).InstanceId;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        var memories = s.GetRequiredService<IStructuredMemoryStore>();
        var sources = new List<StructuredMemoryItem>();
        async Task Add(MemoryKind kind, string subject, string content, MemoryScope scope = MemoryScope.IdentityUser)
        {
            var item = new StructuredMemoryItem(Guid.NewGuid(), session.SessionId, kind, MemoryItemStatus.Active, subject, content,
                StructuredMemoryItem.SubjectKeyFor(subject), new("agent_inferred", [], null, now), now, now, scope,
                scope == MemoryScope.User ? null : id, LocalUserProfile.Id);
            await memories.InsertAsync(item); sources.Add(item);
        }
        await Add(MemoryKind.Preference, "Aurora frontend language", "For fictional Aurora React frontend examples, prefer TypeScript.");
        await Add(MemoryKind.Preference, "Aurora frontend samples", "For fictional Aurora React frontend code samples, prefer TypeScript.");
        foreach (var scope in new[] { MemoryScope.User, MemoryScope.IdentityUser })
            await Add(MemoryKind.Decision, "Aurora Scrum decision", "Use fictional Scrum checklists for Aurora.", scope);
        await Add(MemoryKind.Goal, "Aurora accessibility", "Review keyboard accessibility next sprint.");
        await Add(MemoryKind.OpenLoop, "Aurora launch review", "The fabricated launch review is still awaiting owner feedback.");
        foreach (var (subject, content) in new[] { ("Aurora reporting", "Keep fictional weekly reports concise."),
            ("Aurora test data", "Use fabricated customer names in tests."), ("Aurora language", "Examples should be in English."),
            ("Aurora documentation", "Keep acceptance criteria alongside the fictional feature description.") })
            await Add(MemoryKind.Preference, subject, content);
        var experiences = s.GetRequiredService<IExperienceStore>();
        await experiences.ConfigureAsync(id, 0, true); await experiences.ConfigureMaintenanceAsync(id, 0, true);
        var distinct = new List<AgentExperience>();
        foreach (var (goal, attempt, lesson) in new[] {
            ("Aurora Scrum planning with no JIRA integration", "Prepared a local Markdown backlog; no issue service was used", "Keep the JIRA-free process local and do not introduce issue-service dependencies."),
            ("Aurora Scrum planning specifically with JIRA issue links", "Matched issue keys and checked the configured JIRA workflow states", "Preserve the exact JIRA issue keys and workflow transitions."),
            ("Aurora deployment rehearsal only; no Scrum planning", "Rehearsed a fabricated deployment rollback", "Verify the rollback procedure independently of sprint planning.") })
        {
            var source = await ExperienceJourneyTests.SeedAsync(s, id);
            var eid = Guid.NewGuid();
            var e = new AgentExperience(eid, id, LocalUserProfile.Id, ExperienceSourceKind.Session, source.SessionId, 2, now,
                "general-assistant", 16, eid, new("deepseek-v41-flash", "primary-llm", options.DefaultModel, null), now);
            await experiences.AdmitAsync(e);
            await experiences.CompleteAsync(id, eid, new(goal, [attempt], [], ["Recorded the specific project context"], [], [], [], [lesson]));
            distinct.Add(e);
        }
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 16))!;
        var search = new ModelToolCall("budget-check", ToolCatalog.ContinuitySearch, """{"query":"Aurora","limit":10}""");
        var partial = await s.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition, Guid.Empty, search, 1500,
            admission: new(true, TriggerKind.ManualInvocation, AgentInstanceId: id));
        using (var json = JsonDocument.Parse(partial.Text))
        {
            Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
            Assert.NotEmpty(json.RootElement.GetProperty("result").EnumerateArray());
            Assert.DoesNotContain("output_limit", partial.Text);
            output.WriteLine("Reduced-budget search retained {0} complete records in {1} bytes.", json.RootElement.GetProperty("result").GetArrayLength(), Encoding.UTF8.GetByteCount(partial.Text));
        }
        var thoughts = s.GetRequiredService<AdminAutomationAuthoringService>();
        var r = await thoughts.SaveAsync(id, null, 0, true, 3600, "try to consolidate your memory and experience", null, null);
        await thoughts.RunNowAsync(id, r.AutomationId, r.Revision); await ThoughtJourneyTests.Intake(s);
        await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
        var work = (await s.GetRequiredService<IWorkItemStore>().ListAsync(new(id, LocalUserProfile.Id), 100)).Single(w => w.Provenance.SourceKind == WorkSourceKind.ManualInvocation);
        output.WriteLine("Real Thought status={0}; outcome={1}; checkpointBytes={2}; calls={3}; failure={4}", work.Status,
            work.Result is null ? null : WorkCompletionRequest.Outcome(work.Result.Text), Encoding.UTF8.GetByteCount(work.Checkpoint!.PayloadJson),
            string.Join(", ", model.Calls.Select(c => c.Name)), work.Failure?.Code);
        Assert.Equal(WorkItemStatus.Completed, work.Status);
        Assert.Contains(WorkCompletionRequest.Outcome(work.Result!.Text), new[] { "NoAction", "ActionCompleted" });
        Assert.False(work.Result.AttentionRequired); Assert.Null(work.Failure);
        Assert.InRange(model.Calls.Count(c => ToolCatalog.IsIdentityMaintenance(c.Name)), 0, 1);
        foreach (var call in model.Calls.Where(c => ToolCatalog.IsIdentityMaintenance(c.Name)))
            output.WriteLine("Maintenance proposal {0}: {1}", call.Name, call.ArgumentsJson);
        foreach (var call in model.Calls.Where(c => c.Name == ToolCatalog.MemoryConsolidate))
        {
            using var args = JsonDocument.Parse(call.ArgumentsJson);
            var selected = args.RootElement.GetProperty("sourceMemoryIds").EnumerateArray().Select(i => sources.Single(m => m.MemoryId == i.GetGuid())).ToArray();
            Assert.Single(selected.Select(m => m.Scope).Distinct()); Assert.Single(selected.Select(m => m.Kind).Distinct());
        }
        foreach (var m in sources.Where(m => m.Kind is MemoryKind.Decision or MemoryKind.Goal or MemoryKind.OpenLoop))
            Assert.Equal(MemoryItemStatus.Active, (m.Scope == MemoryScope.User ? await memories.FindUserAsync(LocalUserProfile.Id, m.MemoryId)
                : await memories.FindIdentityUserAsync(id, LocalUserProfile.Id, m.MemoryId))!.Status);
        foreach (var e in distinct) Assert.Equal(ExperienceVisibility.Eligible, (await experiences.GetAsync(id, e.ExperienceId))!.Visibility);
        Assert.Contains(model.Calls, c => c.Name == ToolCatalog.WorkComplete);
        Assert.All(model.Requests, request => Assert.DoesNotContain(request.Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ContinuitySearch && m.Text.Contains("output_limit")));
        var current = (await s.GetRequiredService<ITriggerStore>().GetAsync(new(id, LocalUserProfile.Id), r.AutomationId))!;
        await thoughts.SaveAsync(id, r.AutomationId, current.Revision, false, 3600, "try to consolidate your memory and experience", null, null);
    }
    private sealed class Resolver(ILanguageModel model) : ILanguageModelResolver
    { public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose) => model; }
    private sealed class ObservedModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelCapabilities Capabilities => inner.Capabilities;
        public List<ModelToolCall> Calls { get; } = [];
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() });
            await foreach (var e in inner.GenerateAsync(request, ct)) { if (e is ModelToolCallEvent call) Calls.Add(call.Call); yield return e; }
        }
    }
}
