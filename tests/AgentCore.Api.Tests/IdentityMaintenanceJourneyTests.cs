using AgentCore.Application.Execution;
using System.Net;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Experience;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using AgentCore.Application.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

[Collection("identity-maintenance-telemetry")]
public sealed class IdentityMaintenanceJourneyTests
{

    private sealed class MaintenanceMetrics : IDisposable
    {
        private readonly MeterListener listener = new();
        internal ConcurrentDictionary<string, long> Outcomes { get; } = new();
        public MaintenanceMetrics()
        {
            listener.InstrumentPublished = (i, l) => { if (i.Name == "identity_maintenance_events") l.EnableMeasurementEvents(i); };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                Assert.Equal(1, tags.Length);
                Assert.Equal("outcome", tags[0].Key);
                var outcome = Assert.IsType<string>(tags[0].Value);
                Outcomes.AddOrUpdate(outcome, value, (_, old) => old + value);
            });
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }

    private static IdentityMaintenanceService Maintenance(IServiceProvider s, AgentDefinition definition) => new(
        s.GetRequiredService<ExperienceService>(), s.GetRequiredService<IExperienceStore>(), s.GetRequiredService<IStructuredMemoryStore>(),
        s.GetRequiredService<IStructuredMemoryService>(), s.GetRequiredService<IMemoryStore>(), new MaintenanceDefinitions(definition), TimeProvider.System);

    private sealed class MaintenanceDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) => ValueTask.FromResult<AgentDefinition?>(definition);
    }

    [Fact(Timeout = 60000)]
    public async Task Contradictory_inferred_memory_Automation_noops_without_alerts_or_mutation()
    {
        using var metrics = new MaintenanceMetrics();
        var db = Path.Combine(Path.GetTempPath(), $"p910-noop-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var id = instance.InstanceId;
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var memory = s.GetRequiredService<IStructuredMemoryService>();
        var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
        var admission = SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, profile, []);
        foreach (var (subject, content) in new[] { ("Frontend TypeScript", "Prefer TypeScript for frontend examples."), ("Frontend Python", "Prefer Python for frontend examples.") })
        {
            var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, content, []), admission);
            await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true, admission);
        }
        var automations = s.GetRequiredService<AdminAutomationAuthoringService>();
        var r = await automations.SaveAsync(id, null, 0, true, 3600, "synthetic-maintain-memory", null, null);
        await automations.RunNowAsync(id, r.AutomationId, r.Revision); await AutomationJourneyTests.Intake(s);
        await s.ExecuteRunsAsync(100);
        var store = s.GetRequiredService<IAgentRunStore>();
        var work = Assert.Single(await store.ListAsync(new(id, LocalUserProfile.Id), 100));
        Assert.Equal(AgentRunStatus.Completed, work.Status);
        Assert.Equal("NoAction", work.Result!.OutcomeKind.ToString());
        Assert.False(work.Result.AttentionRequired);
        Assert.Empty(metrics.Outcomes);
        Assert.False(work.Result?.AttentionRequired ?? false);
        Assert.Equal(2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
    }

    [Fact(Timeout = 60000)]
    public async Task Invalid_arguments_sensitive_content_owner_overrides_and_execution_origins_never_mutate()
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-validation-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var id = instance.InstanceId;
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var memory = s.GetRequiredService<IStructuredMemoryService>();
        var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
        var admission = SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, profile, []);
        var sources = new List<Guid>();
        foreach (var subject in new[] { "Frontend A", "Frontend B" })
        {
            var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, "Prefer TypeScript for frontend examples.", []), admission);
            sources.Add((await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true, admission)).MemoryId);
        }
        var tools = s.GetRequiredService<SessionToolExecutor>();
        var automation = new ToolExecutionAdmission(true, TriggerKind.ManualInvocation, AgentInstanceId: id);
        object Payload(string content = "Prefer TypeScript for frontend examples.") => new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend examples", content };
        var invalid = new[] { "[]", JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Fact", subject = "Frontend examples", content = "Wrong kind" }),
            JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend examples", content = "Untrusted", ownerInstanceId = Guid.NewGuid() }),
            JsonSerializer.Serialize(Payload("sk-abcdefghijklmnopqrst")), JsonSerializer.Serialize(Payload(new string('x', 2001))) };
        foreach (var args in invalid)
        {
            var result = await tools.ExecuteAsync(definition, Guid.Empty, new("invalid", ToolCatalog.MemoryConsolidate, args), 8000, admission: automation);
            Assert.Contains("error", result.Text);
            Assert.Equal(2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
        }
        var valid = new ModelToolCall("valid", ToolCatalog.MemoryConsolidate, JsonSerializer.Serialize(Payload()));
        foreach (var denied in new[] { automation with { SupportsTools = false }, automation with { TriggerKind = TriggerKind.LongSilence } })
            Assert.Contains("forbidden", (await tools.ExecuteAsync(definition, Guid.Empty, valid, 8000, admission: denied)).Text);
        // Schema constraints supplied as properties receive actionable validation, never relaxed acceptance.
        var extra = valid with { ArgumentsJson = JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend examples", content = "Safe", minItems = 2 }) };
        var argsElement = JsonSerializer.Deserialize<JsonElement>(extra.ArgumentsJson);
        Assert.Equal(ToolPolicyDecision.Allow, await tools.EvaluateExecutionPolicyAsync(definition, Guid.Empty, extra, argsElement, automation, default));
        Assert.Contains("Unsupported maintenance argument", (await tools.ExecuteAsync(definition, Guid.Empty, extra, 8000, admission: automation)).Text);
        Assert.Equal(2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
    }
}

[CollectionDefinition("identity-maintenance-telemetry", DisableParallelization = true)]
public sealed class IdentityMaintenanceTelemetryCollection;
