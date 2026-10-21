using AgentCore.Application.Execution;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Experience;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Api.Tests;

/// <summary>Explicit opt-in semantic acceptance, using fabricated state and the actual Real model/tool loop.</summary>
public sealed class LiveIdentityMaintenanceTests(ITestOutputHelper output)
{
    private sealed class LiveResolver(ILanguageModel model) : ILanguageModelResolver
    { public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose) => model; }
    internal static string? Key() => new ConfigurationBuilder().AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables().Build()["OPENROUTER_API_KEY"];

    [LiveIdentityMaintenanceFact(Timeout = 480000)]
    public async Task Real_profile_repeated_Experience_and_inferred_Memory_contradiction()
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-real-{Guid.NewGuid():N}.db");
        var options = new LanguageModelProviderOptions { Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/", ApiKey = Key(), DefaultModel = "deepseek/deepseek-v4.1-flash", Tools = true, StructuredOutput = false };
        using var http = new HttpClient();
        var model = new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, options));
        await using var host = new ExperienceHost(db, languageModel: model, configure: services => {
            services.RemoveAll<IModelCatalog>(); services.AddSingleton<IModelCatalog>(ModelCatalogFactory.Create("Real", options, null));
            services.RemoveAll<ILanguageModelResolver>(); services.AddSingleton<ILanguageModelResolver>(new LiveResolver(model));
        }, configuration: c => c.AddInMemoryCollection(new Dictionary<string, string?> {
            ["AgentCore:Profile"] = "Real", ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
            ["Providers:LanguageModels:primary-llm:BaseUrl"] = "https://openrouter.ai/api/v1/",
            ["Providers:LanguageModels:primary-llm:DefaultModel"] = "deepseek/deepseek-v4.1-flash",
            ["Providers:LanguageModels:primary-llm:ApiKey"] = Key(), ["Providers:LanguageModels:primary-llm:Tools"] = "true",
            ["Providers:LanguageModels:primary-llm:StructuredOutput"] = "false", ["Providers:LanguageModels:primary-llm:ReasoningEffort"] = null
        }));
        var s = host.Services;
        var client = host.CreateClient();
        Assert.Contains("Real", await client.GetStringAsync("/health"));
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var id = instance.InstanceId;
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
        await s.GetRequiredService<IExperienceStore>().ConfigureAsync(id, 0, true);
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        var memory = s.GetRequiredService<IStructuredMemoryService>();
        var memories = s.GetRequiredService<IStructuredMemoryStore>();
        var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
        async Task SeedMemories(Guid owner, (string subject, string text)[] contents)
        {
            var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(owner, SessionMode.Text);
            var admission = SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, profile, []);
            foreach (var (subject, content) in contents)
            {
                var record = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, content, []), admission);
                await memory.PromoteToIdentityUserAsync(new(session.SessionId), record.MemoryId, new(owner, LocalUserProfile.Id), true, admission);
            }
        }
        async Task<AgentRun> Automation(Guid owner, string prompt)
        {
            var automations = s.GetRequiredService<AdminAutomationAuthoringService>();
            var r = await automations.SaveAsync(owner, null, 0, true, 3600, prompt + " Finish via work.complete with a summary under 300 characters, valid outcome and attentionRequired=false.", null, null);
            await automations.RunNowAsync(owner, r.AutomationId, r.Revision); await AutomationJourneyTests.Intake(s);
            await s.ExecuteRunsAsync(100);
            var result = Assert.Single(await s.AutomationRunsAsync(new(owner, LocalUserProfile.Id), r.AutomationId));
            output.WriteLine($"Automation status={result.Status}; outcome={result.Result?.Text}; failure={result.Failure?.Summary}");
            Assert.Equal(AgentRunStatus.Completed, result.Status);
            return result;
        }
        // Fabricated source conversations go through ordinary Experience admission and the Real retrospective generator.
        for (var i = 0; i < 3; i++)
        {
            var source = await ExperienceTestSessions.SeedAsync(s, id);
            source = source with { Revision = source.Revision + 1, Entries = source.Entries.Select(e => e with { Text = e.Text + " This observation concerns the internal React dashboard only." }).ToArray() };
            await s.GetRequiredService<IMemoryStore>().SaveAsync(source, source.Revision - 1);
            await s.GetRequiredService<ExperienceService>().RequestSessionAsync(id, source.SessionId);
        }
        await s.ExecuteRunsAsync(100);
        var sources = (await s.GetRequiredService<IExperienceStore>().ListAsync(id, 100)).Where(e => e.Content is not null).ToArray();
        Assert.Equal(3, sources.Length);
        await Automation(id, "Inspect recent Experience about the internal React dashboard. If the three observations genuinely repeat, consolidate them with experience.consolidate. Preserve the dashboard qualifier, failed initial approaches, correction to observe the current page, and unresolved work. Never promote Experience to Memory. Complete with Response only after the tool confirms the mutation.");
        var experiences = await s.GetRequiredService<IExperienceStore>().ListAsync(id, 100);
        var generalized = Assert.Single(experiences, e => e.DerivedFromExperienceIds is { Count: 3 });
        output.WriteLine("Generalized Experience: " + System.Text.Json.JsonSerializer.Serialize(generalized.Content));
        Assert.All(sources, e => Assert.Contains(e.ExperienceId, generalized.DerivedFromExperienceIds!));
        Assert.Contains("dashboard", System.Text.Json.JsonSerializer.Serialize(generalized.Content), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await memories.ListActiveIdentityUserAsync(id, LocalUserProfile.Id));
        await SeedMemories(id, [("Frontend language", "For the internal React dashboard, prefer TypeScript in frontend examples."),
            ("Frontend samples", "Use TypeScript for frontend code samples in the internal React dashboard."),
            ("Frontend examples", "Frontend examples for the internal React dashboard should use TypeScript.")]);
        await Automation(id, "Inspect the three learned frontend preferences. Consolidate only genuinely redundant inferred IdentityUser Preference records into one canonical memory with memory.consolidate. Keep the internal React dashboard qualifier. Inspect each exact source using continuity.get. Pass only sourceMemoryIds, kind, subject, content; minItems and maxItems are schema constraints and must not be arguments. Preserve source lineage and report Response only after confirmed mutation.");
        var canonical = Assert.Single(await memories.ListActiveIdentityUserAsync(id, LocalUserProfile.Id));
        Assert.Equal(3, canonical.Provenance.DerivedFromMemoryIds!.Count);
        Assert.Contains("dashboard", canonical.Content, StringComparison.OrdinalIgnoreCase);
        output.WriteLine("Canonical memory: " + canonical.Content);
        var other = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(other.InstanceId, 0, true);
        await SeedMemories(other.InstanceId, [("Frontend language A", "Prefer TypeScript for all frontend examples."), ("Frontend language B", "Prefer Python for all frontend examples.")]);
        var noOp = await Automation(other.InstanceId, "Inspect learned frontend preferences for contradictions. With insufficient evidence to choose one, do not consolidate or forget either. Complete with NoAction and explain that current user clarification is needed.");
        Assert.Equal("NoAction", noOp.Result!.OutcomeKind.ToString());
        Assert.Equal(2, await memories.CountActiveIdentityUserAsync(other.InstanceId, LocalUserProfile.Id));


    }
}

public sealed class LiveIdentityMaintenanceFactAttribute : FactAttribute
{
    public LiveIdentityMaintenanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_IDENTITY_MAINTENANCE") != "1") Skip = "Explicit AGENTCORE_LIVE_IDENTITY_MAINTENANCE=1 required; default gates remain key-free.";
        else if (string.IsNullOrWhiteSpace(LiveIdentityMaintenanceTests.Key())) Skip = "OpenRouter key is missing.";
    }
}
