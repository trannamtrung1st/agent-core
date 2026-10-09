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
    [Theory(Timeout = 60000)]
    [InlineData(MemoryScope.Session)]
    [InlineData(MemoryScope.IdentityUser)]
    [InlineData(MemoryScope.User)]
    public async Task Resolved_memory_survives_restart_and_forgetting_still_requires_exact_approval(MemoryScope scope)
    {
        var db = Path.Combine(Path.GetTempPath(), $"memory-resolution-{Guid.NewGuid():N}.db");
        Guid instanceId, sessionId, memoryId;
        await using (var host = new ExperienceHost(db))
        {
            var services = host.Services;
            instanceId = (await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21)).InstanceId;
            var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = session.SessionId;
            var memories = services.GetRequiredService<IStructuredMemoryService>();
            var context = SessionMemoryPrompt.CreateAdmissionContext("user_explicit", definition,
                await services.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id), []);
            var original = await memories.WriteAsync(new(sessionId), new(MemoryKind.OpenLoop, "Research", "Keep /home/report.md", []), context);
            var item = scope switch
            {
                MemoryScope.IdentityUser => await memories.PromoteToIdentityUserAsync(new(sessionId), original.MemoryId, new(instanceId, LocalUserProfile.Id), true, context),
                MemoryScope.User => await memories.PromoteSessionToUserAsync(new(sessionId), original.MemoryId, new(LocalUserProfile.Id), true, context),
                _ => original
            };
            memoryId = item.MemoryId;
            var resolution = await memories.ResolveOpenLoopsAsync(new(sessionId), new(instanceId, LocalUserProfile.Id), new(LocalUserProfile.Id), "Research");
            Assert.All(resolution.Items, retained => Assert.Equal(MemoryItemStatus.Resolved, retained.Status));
            var client = TestOwnerCapability.CreateOwnerClient(host);
            (await client.PutAsync($"/api/v2/sessions/{sessionId}/workspace/content?path=/home/report.md", new ByteArrayContent("Retained report"u8.ToArray()))).EnsureSuccessStatusCode();
        }
        await using (var reopened = new ExperienceHost(db))
        {
            var services = reopened.Services;
            var store = services.GetRequiredService<IStructuredMemoryStore>();
            async Task<StructuredMemoryItem> ReadAsync() => (scope switch
            {
                MemoryScope.Session => await store.FindAsync(sessionId, memoryId),
                MemoryScope.IdentityUser => await store.FindIdentityUserAsync(instanceId, LocalUserProfile.Id, memoryId),
                _ => await store.FindUserAsync(LocalUserProfile.Id, memoryId)
            })!;
            var retained = await ReadAsync();
            Assert.Equal(MemoryItemStatus.Resolved, retained.Status);
            Assert.Equal("Keep /home/report.md", retained.Content);
            var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            definition = definition with { MemoryPolicy = definition.MemoryPolicy! with { UserRetrieval = true } };
            var service = Maintenance(services, definition);
            var args = JsonSerializer.SerializeToElement(new { memoryId });
            var call = new ModelToolCall("forget-resolved", ToolCatalog.MemoryForget, args.GetRawText());
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instanceId);
            var missing = await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(definition, sessionId, call, args, admission, null, default).AsTask());
            Assert.Equal("ApprovalRequired", missing.Code);
            var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args), Guid.Empty, Guid.Empty, Guid.Empty);
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(definition, sessionId, call, args, admission,
                grant with { ActionHash = "wrong-action" }, default).AsTask());
            Assert.Equal(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(await ReadAsync()));
            var outcome = JsonSerializer.SerializeToElement(await service.ExecuteAsync(definition, sessionId, call, args, admission, grant, default));
            Assert.Equal("forgotten", outcome.GetProperty("status").GetString());
            Assert.Equal(MemoryItemStatus.Deleted, (await ReadAsync()).Status);
            Assert.Equal("", (await ReadAsync()).Content);
            var client = TestOwnerCapability.CreateOwnerClient(reopened);
            Assert.Equal("Retained report", await client.GetStringAsync($"/api/v2/sessions/{sessionId}/workspace/content?path=/home/report.md"));
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Shared_User_memory_exact_replay_crosses_instances_and_SQLite_restart()
    {
        using var metrics = new MaintenanceMetrics();
        var db = Path.Combine(Path.GetTempPath(), $"p910-user-replay-{Guid.NewGuid():N}.db");
        Guid firstId, secondId, resultId;
        ModelToolCall call;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var admin = s.GetRequiredService<AdminAgentInstanceService>();
            firstId = (await admin.CreateManagedAsync("general-assistant", 21)).InstanceId;
            secondId = (await admin.CreateManagedAsync("general-assistant", 21)).InstanceId;
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            definition = definition with { MemoryPolicy = definition.MemoryPolicy! with { UserRetrieval = true, UserPromotion = true } };
            var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(firstId, SessionMode.Text);
            var memory = s.GetRequiredService<IStructuredMemoryService>();
            var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
            var admission = SessionMemoryPrompt.CreateAdmissionContext("user_explicit", definition, profile, []);
            var sources = new List<Guid>();
            foreach (var subject in new[] { "Shared frontend language", "Shared frontend samples" })
            {
                var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, "Prefer TypeScript for frontend examples.", []), admission);
                sources.Add((await memory.PromoteSessionToUserAsync(new(session.SessionId), source.MemoryId, new(LocalUserProfile.Id), true, admission)).MemoryId);
            }
            call = new("shared", ToolCatalog.MemoryConsolidate, JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Preference", subject = "Shared frontend preference", content = "Prefer TypeScript for frontend examples." }));
            var args = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson);
            var service = Maintenance(s, definition);
            var origin = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: firstId);
            var tools = new SessionToolExecutor(identityMaintenance: service);
            var prepared = await ToolActionPreparation.PrepareApprovalAsync(tools, call, args, default, definition, session.SessionId, origin);
            Assert.Equal("User-wide", prepared.Preparation!.Details!["Scope"]);
            Assert.Contains("other Agent Instances", prepared.Preparation.Details["Cross-agent effect"]);
            Assert.Equal("Preference", prepared.Preparation.Details["Memory kind"]);
            Assert.Contains("Shared frontend samples", prepared.Preparation.Details["Source subjects"]);
            Assert.Equal(ToolActionHash.Compute(call.Name, args), prepared.Preparation.ActionHash);
            var forget = new ModelToolCall("forget-shared", ToolCatalog.MemoryForget, JsonSerializer.Serialize(new { memoryId = sources[0] }));
            var forgetPreview = await ToolActionPreparation.PrepareApprovalAsync(tools, forget, JsonSerializer.Deserialize<JsonElement>(forget.ArgumentsJson), default, definition, session.SessionId, origin);
            Assert.Equal("User-wide", forgetPreview.Preparation!.Details!["Scope"]);
            Assert.Contains("other Agent Instances", forgetPreview.Preparation.Details["Cross-agent effect"]);
            var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args), Guid.Empty, Guid.Empty, Guid.Empty);
            var first = JsonSerializer.SerializeToElement(await service.ExecuteAsync(definition, session.SessionId, call, args, origin, grant, default));
            resultId = first.GetProperty("memoryId").GetGuid();
            var replay = JsonSerializer.SerializeToElement(await service.ExecuteAsync(definition, Guid.Empty, call, args, origin with { AgentInstanceId = secondId }, grant, default));
            Assert.Equal(resultId, replay.GetProperty("memoryId").GetGuid());
            var changed = JsonSerializer.SerializeToElement(new { sourceMemoryIds = sources, kind = "Preference", subject = "Shared frontend preference", content = "Prefer JavaScript instead." });
            var conflict = await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(definition, Guid.Empty, call, changed,
                origin with { AgentInstanceId = secondId }, grant with { ActionHash = ToolActionHash.Compute(call.Name, changed) }, default).AsTask());
            Assert.Equal(409, conflict.StatusCode);
            var canonical = Assert.Single(await s.GetRequiredService<IStructuredMemoryStore>().ListActiveUserAsync(LocalUserProfile.Id));
            Assert.Equal(resultId, canonical.MemoryId);
            Assert.Equal(firstId, canonical.Provenance.MaintenanceAgentInstanceId);
            Assert.Equal(session.SessionId, canonical.Provenance.MaintenanceSessionId);
            Assert.Null(canonical.Provenance.OriginSessionId);
            Assert.Null(canonical.Provenance.MaintenanceAgentRunId);
        }
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            definition = definition with { MemoryPolicy = definition.MemoryPolicy! with { UserRetrieval = true } };
            var args = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson);
            var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args), Guid.Empty, Guid.Empty, Guid.Empty);
            var replay = JsonSerializer.SerializeToElement(await Maintenance(s, definition).ExecuteAsync(definition, Guid.Empty, call, args,
                new(false, TriggerKind.UserTurn, AgentInstanceId: secondId), grant, default));
            Assert.Equal(resultId, replay.GetProperty("memoryId").GetGuid());
            Assert.Equal(firstId, (await s.GetRequiredService<IStructuredMemoryStore>().FindUserAsync(LocalUserProfile.Id, resultId))!.Provenance.MaintenanceAgentInstanceId);
        }
        Assert.Equal(4, metrics.Outcomes["attempted"]);
        Assert.Equal(3, metrics.Outcomes["completed"]);
        Assert.Equal(1, metrics.Outcomes["conflict"]);
        Assert.Equal(3, metrics.Outcomes.Count);
    }

    [Theory(Timeout = 60000)]
    [InlineData(MemoryScope.Session, "This Session")]
    [InlineData(MemoryScope.IdentityUser, "This Agent Instance and trusted user profile")]
    public async Task Scope_previews_keep_complete_bounded_sources_and_replacement_and_rejections_are_measured(MemoryScope scope, string expectedScope)
    {
        using var metrics = new MaintenanceMetrics();
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"p910-preview-{Guid.NewGuid():N}.db"));
        var s = host.Services;
        var id = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21)).InstanceId;
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var memory = s.GetRequiredService<IStructuredMemoryService>();
        var context = SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id), []);
        var sources = new List<Guid>();
        foreach (var i in Enumerable.Range(0, 8))
        {
            var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, $"Frontend samples {i} with project qualifiers, tested versions, temporal context and retained exceptions", "Prefer TypeScript for frontend examples.", []), context);
            sources.Add(scope == MemoryScope.Session ? source.MemoryId : (await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true, context)).MemoryId);
        }
        var content = new string('a', 1900) + " final qualifier";
        var call = new ModelToolCall("bounded-preview", ToolCatalog.MemoryConsolidate, JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend samples", content }));
        var args = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson);
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: id);
        var service = Maintenance(s, definition);
        var tools = new SessionToolExecutor(identityMaintenance: service);
        var prepared = await ToolActionPreparation.PrepareApprovalAsync(tools, call, args, default, definition, session.SessionId, admission);
        Assert.Equal(expectedScope, prepared.Preparation!.Details!["Scope"]);
        Assert.False(prepared.Preparation.Details.ContainsKey("Cross-agent effect"));
        Assert.EndsWith("final qualifier", prepared.Preparation.Details["Replacement"]);
        foreach (var source in sources) Assert.Contains(source.ToString(), prepared.Preparation.Details["Source subjects"]);
        var preview = prepared.Preparation.Preview + "\n" + string.Join("\n", prepared.Preparation.Details.Select(d => $"{d.Key}: {d.Value}"));
        Assert.True(preview.Length > 2000);
        var now = DateTimeOffset.UtcNow;
        var durable = new AgentRunApproval(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, call.Name, call.ArgumentsJson, prepared.Preparation.ActionHash,
            preview, now.AddMinutes(5), AgentRunApprovalDecision.Pending, null, false, 1, now);
        Assert.Equal(preview, durable.Preview);
        Assert.Contains("forbidden", (await ToolActionPreparation.PrepareApprovalAsync(tools, call, args)).ErrorJson);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(definition, session.SessionId, call, args, admission, null, default).AsTask());
        Assert.Equal(1, metrics.Outcomes["rejected_by_policy"]);
        var forget = new ModelToolCall("forget-preview", ToolCatalog.MemoryForget, JsonSerializer.Serialize(new { memoryId = sources[0] }));
        var forgetArgs = JsonSerializer.Deserialize<JsonElement>(forget.ArgumentsJson);
        var forgetPreview = await ToolActionPreparation.PrepareApprovalAsync(tools, forget, forgetArgs, default, definition, session.SessionId, admission);
        Assert.Equal(expectedScope, forgetPreview.Preparation!.Details!["Scope"]);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(definition, session.SessionId, forget, forgetArgs, admission, null, default).AsTask());
        Assert.Equal(1, metrics.Outcomes["forget_rejected"]);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), forget.Name, ToolActionHash.Compute(forget.Name, forgetArgs), Guid.Empty, Guid.Empty, Guid.Empty);
        var forgotten = JsonSerializer.SerializeToElement(await service.ExecuteAsync(definition, session.SessionId, forget, forgetArgs, admission, grant, default));
        Assert.Equal("forgotten", forgotten.GetProperty("status").GetString());
        await service.ExecuteAsync(definition, session.SessionId, forget, forgetArgs, admission, grant, default);
        Assert.Equal(2, metrics.Outcomes["forget_completed"]);
        Assert.Equal(4, metrics.Outcomes["attempted"]);
        Assert.All(metrics.Outcomes.Keys, k => Assert.Contains(k, new[] { "attempted", "rejected_by_policy", "forget_rejected", "forget_completed" }));
    }

    [Fact(Timeout = 60000)]
    public async Task Legacy_exact_retry_matches_the_original_operation_hash_and_keeps_unknown_initiator()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"p910-legacy-{Guid.NewGuid():N}.db"));
        var s = host.Services;
        var id = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21)).InstanceId;
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var memory = s.GetRequiredService<IStructuredMemoryService>();
        var context = SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id), []);
        var sources = new List<StructuredMemoryItem>();
        foreach (var title in new[] { "Legacy frontend language", "Legacy frontend samples" })
        {
            var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, title, "Prefer TypeScript for frontend examples.", []), context);
            sources.Add(await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true, context));
        }
        var lineage = sources.Select(m => m.MemoryId).Order().ToArray();
        var kind = MemoryKind.Preference;
        var subject = "Legacy frontend preference";
        var content = "Prefer TypeScript for frontend examples.";
        var legacyPayload = JsonSerializer.Serialize(new { sources[0].Scope, lineage, kind, subject, content }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var legacyId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"agent-core:identity-maintenance:v1:{id:D}:memory:" + legacyPayload)).AsSpan(0, 16));
        var store = s.GetRequiredService<IStructuredMemoryStore>();
        await store.ConsolidateAsync(sources, sources[0] with { MemoryId = legacyId, Subject = subject, SubjectKey = StructuredMemoryItem.SubjectKeyFor(subject),
            Content = content, Provenance = new("agent_inferred", [], null, DateTimeOffset.UtcNow, DerivedFromMemoryIds: lineage, MaintenanceOrigin: "UserTurn") });
        var args = JsonSerializer.SerializeToElement(new { sourceMemoryIds = lineage, kind = "Preference", subject, content });
        var call = new ModelToolCall("legacy-retry", ToolCatalog.MemoryConsolidate, args.GetRawText());
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: id);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args), Guid.Empty, Guid.Empty, Guid.Empty);
        var service = Maintenance(s, definition);
        var retry = JsonSerializer.SerializeToElement(await service.ExecuteAsync(definition, session.SessionId, call, args, admission, grant, default));
        Assert.Equal(legacyId, retry.GetProperty("memoryId").GetGuid());
        Assert.Null((await store.FindIdentityUserAsync(id, LocalUserProfile.Id, legacyId))!.Provenance.MaintenanceAgentInstanceId);
        // Even if a later owner edit matches a new proposal, the old operation identity cannot authorize replay of it.
        var canonical = (await store.FindIdentityUserAsync(id, LocalUserProfile.Id, legacyId))!;
        var edited = canonical with { MemoryId = Guid.NewGuid(), Content = "Prefer JavaScript instead.",
            Provenance = canonical.Provenance with { SupersedesMemoryId = canonical.MemoryId } };
        await store.SupersedeAsync(canonical with { Status = MemoryItemStatus.Superseded }, edited);
        var changed = JsonSerializer.SerializeToElement(new { sourceMemoryIds = lineage, kind = "Preference", subject, content = "Prefer JavaScript instead." });
        var conflict = await Assert.ThrowsAsync<AgentCoreException>(() => service.ExecuteAsync(definition, session.SessionId, call, changed, admission,
            grant with { ActionHash = ToolActionHash.Compute(call.Name, changed) }, default).AsTask());
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("Prefer JavaScript instead.", (await store.FindIdentityUserAsync(id, LocalUserProfile.Id, edited.MemoryId))!.Content);
    }

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

    [Theory(Timeout = 90000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Protected_Automation_approval_survives_restart_and_late_delete_or_opt_out_wins(bool optOut)
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-approval-{Guid.NewGuid():N}.db");
        Guid id, workId, sourceId;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
            id = instance.InstanceId;
            await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
            var memory = s.GetRequiredService<IStructuredMemoryService>();
            var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
            var admission = SessionMemoryPrompt.CreateAdmissionContext("user_explicit", definition, profile, []);
            sourceId = Guid.Empty;
            foreach (var subject in new[] { "Frontend language", "Frontend samples", "Frontend code" })
            {
                var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, "Prefer TypeScript for frontend examples.", []), admission);
                sourceId = (await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true, admission)).MemoryId;
            }
            var automations = s.GetRequiredService<AdminAutomationAuthoringService>();
            var r = await automations.SaveAsync(id, null, 0, true, 3600, "synthetic-maintain-memory", null, null);
            await automations.RunNowAsync(id, r.AutomationId, r.Revision);
            await AutomationJourneyTests.Intake(s);
            await s.ExecuteRunsAsync(100);
            var work = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(id, LocalUserProfile.Id), 100));
            workId = work.AgentRunId;
            Assert.Equal(AgentRunStatus.WaitingForApproval, work.Status);
            Assert.Equal(ToolCatalog.MemoryConsolidate, work.Approval!.ToolName);
            var preview = JsonSerializer.Deserialize<JsonElement>(work.Approval.Preview);
            Assert.Equal("This Agent Instance and trusted user profile", preview.GetProperty("details").GetProperty("Scope").GetString());
            Assert.True(preview.GetProperty("details").TryGetProperty("Source subjects", out _));
            Assert.Contains("Frontend samples", work.Approval.Preview);
            Assert.Equal(3, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
        }
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var store = s.GetRequiredService<IAgentRunStore>();
            var work = (await store.GetAsync(new(id, LocalUserProfile.Id), workId))!;
            var a = work.Approval!;
            Assert.Equal(AgentRunStatus.WaitingForApproval, work.Status);
            if (optOut) await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 1, false);
            else await s.GetRequiredService<IStructuredMemoryService>().DeleteIdentityUserAsync(new(id, LocalUserProfile.Id), sourceId, true);
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var url = $"/api/v2/sessions/{work.SessionId}/agent-runs/{workId}/approvals/{a.ApprovalId}/approve";
            (await client.PostAsJsonAsync(url, new DecideAgentRunApprovalRequest(work.Revision, a.Revision, a.ActionHash))).EnsureSuccessStatusCode();
            await s.ExecuteRunsAsync(100);
            Assert.Equal(optOut ? 3 : 2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
            Assert.DoesNotContain(await s.GetRequiredService<IStructuredMemoryStore>().ListActiveIdentityUserAsync(id, LocalUserProfile.Id), m => m.Provenance.DerivedFromMemoryIds is { Count: > 0 });
            if (!optOut) Assert.Equal(MemoryItemStatus.Deleted, (await s.GetRequiredService<IStructuredMemoryStore>().FindIdentityUserAsync(id, LocalUserProfile.Id, sourceId))!.Status);
        }
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

    [Fact(Timeout = 90000)]
    public async Task Owned_atomic_consolidation_exact_authority_fresh_recall_and_SQLite_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-journey-{Guid.NewGuid():N}.db");
        Guid id, resultId, experienceId, sourceId;
        ModelToolCall memoryCall;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
            id = instance.InstanceId;
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{id}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(path + "/maintenance")).StatusCode);
            var settings = await client.GetFromJsonAsync<IdentityMaintenanceSettings>(path + "/maintenance");
            Assert.False(settings!.AllowAgentConsolidation);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(-1, true))).StatusCode);
            (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(0, true))).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(0, false))).StatusCode);
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, instance.ActiveVersion))!;
            var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
            var memory = s.GetRequiredService<IStructuredMemoryService>();
            var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
            var stored = new List<StructuredMemoryItem>();
            foreach (var subject in new[] { "Frontend language", "Frontend samples", "Frontend code" })
            {
                var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, "Prefer TypeScript for frontend examples.", []),
                    SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, profile, []));
                stored.Add(await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true,
                    SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, profile, [])));
            }
            sourceId = stored[0].MemoryId;
            var tools = s.GetRequiredService<SessionToolExecutor>();
            var automation = new ToolExecutionAdmission(true, TriggerKind.ManualInvocation, AgentInstanceId: id, AgentRunId: Guid.NewGuid());
            memoryCall = new("maintenance-memory", ToolCatalog.MemoryConsolidate, JsonSerializer.Serialize(new { sourceMemoryIds = stored.Select(m => m.MemoryId), kind = "Preference", subject = "Frontend language", content = "Prefer TypeScript for frontend examples." }));
            var args = JsonSerializer.Deserialize<JsonElement>(memoryCall.ArgumentsJson);
            Assert.Equal(ToolPolicyDecision.Allow, await tools.EvaluateExecutionPolicyAsync(definition, Guid.Empty, memoryCall, args, automation, default));
            var response = await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: automation);
            var data = JsonSerializer.Deserialize<JsonElement>(response.Text);
            Assert.Equal("consolidated", data.GetProperty("status").GetString());
            resultId = data.GetProperty("memoryId").GetGuid();
            Assert.Equal(resultId, JsonSerializer.Deserialize<JsonElement>((await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: automation)).Text).GetProperty("memoryId").GetGuid());
            Assert.Equal(1, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
            var historical = (await client.GetFromJsonAsync<AdminLearnedMemoryItemResponse>(path + $"/learned-memory/{sourceId}?scope=IdentityUser"))!;
            Assert.Equal("Superseded", historical.Status);
            Assert.Contains("TypeScript", historical.Content);
            var active = (await client.GetFromJsonAsync<AdminLearnedMemoryListResponse>(path + "/learned-memory?scope=IdentityUser"))!;
            var activeItem = Assert.Single(active.Items);
            Assert.Equal(3, activeItem.Provenance.DerivedFromMemoryIds!.Count);
            Assert.Equal(id.ToString("D"), activeItem.Provenance.MaintenanceAgentInstanceId);
            Assert.Equal(automation.AgentRunId!.Value.ToString("D"), activeItem.Provenance.MaintenanceAgentRunId);
            Assert.Null(activeItem.Provenance.MaintenanceSessionId);
            var fresh = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
            var recall = await SessionMemoryPrompt.LoadAsync(memory, fresh.SessionId, definition, profile, [], agentInstanceId: id);
            Assert.Equal(resultId, Assert.Single(recall).MemoryId);
            var continuity = await s.GetRequiredService<ContinuityService>().SearchAsync(id, "frontend", includeSessions: false);
            Assert.Equal(resultId, Assert.Single(continuity).Id);

            // A combined identity memory is independently owned, not a promotion from one Session.
            (await client.DeleteAsync($"/api/v2/sessions/{session.SessionId}")).EnsureSuccessStatusCode();
            Assert.Equal(resultId, Assert.Single(await s.GetRequiredService<IStructuredMemoryStore>().ListActiveIdentityUserAsync(id, LocalUserProfile.Id)).MemoryId);
            Assert.Equal(resultId, Assert.Single(await s.GetRequiredService<ContinuityService>().SearchAsync(id, "frontend", includeSessions: false)).Id);

            var ex = s.GetRequiredService<IExperienceStore>();
            await ex.ConfigureAsync(id, 0, true);
            var records = new List<AgentExperience>();
            for (var i = 0; i < 3; i++)
            {
                var source = await ExperienceJourneyTests.SeedAsync(s, id);
                records.Add(await s.GetRequiredService<ExperienceService>().RequestSessionAsync(id, source.SessionId));
            }
            await s.ExecuteRunsAsync(100);
            var call = new ModelToolCall("maintenance-experience", ToolCatalog.ExperienceConsolidate, JsonSerializer.Serialize(new {
                sourceExperienceIds = records.Select(r => r.ExperienceId), goal = "Review repeated browser work", attempts = new[] { "Observed current page" },
                decisions = Array.Empty<string>(), outcomes = new[] { "Observed successful retry" }, corrections = new[] { "Check page state first" },
                unresolved = Array.Empty<string>(), difficulties = new[] { "Earlier approaches failed" }, lessons = new[] { "Observe current page state before browser actions; verify each outcome." } }));
            var output = await tools.ExecuteAsync(definition, Guid.Empty, call, 8000, admission: automation);
            var result = JsonSerializer.Deserialize<JsonElement>(output.Text);
            Assert.Equal("consolidated", result.GetProperty("status").GetString());
            experienceId = result.GetProperty("experienceId").GetGuid();
            var reviews = (await client.GetFromJsonAsync<ExperienceReviewResponse>(path + "/experience"))!;
            Assert.Equal(3, reviews.Items.Count(e => e.Visibility == "Superseded"));
            Assert.Equal(3, reviews.Items.Single(e => e.ExperienceId == experienceId.ToString()).DerivedFromExperienceIds!.Count);
            var context = await s.GetRequiredService<ExperienceService>().RecallAsync(id);
            Assert.Contains(experienceId.ToString(), context);
            foreach (var r in records) Assert.DoesNotContain(r.ExperienceId.ToString(), context);
            Assert.Equal(1, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
            var forbiddenForget = new ModelToolCall("forget", ToolCatalog.MemoryForget, JsonSerializer.Serialize(new { memoryId = resultId }));
            Assert.Contains("ApprovalRequired", (await tools.ExecuteAsync(definition, Guid.Empty, forbiddenForget, 8000, admission: automation)).Text);
            Assert.Equal(1, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
        }
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{id}";
            var settings = (await client.GetFromJsonAsync<IdentityMaintenanceSettings>(path + "/maintenance"))!;
            Assert.True(settings.AllowAgentConsolidation);
            var row = (await client.GetFromJsonAsync<AdminLearnedMemoryItemResponse>(path + $"/learned-memory/{resultId}?scope=IdentityUser"))!;
            Assert.Equal(3, row.Provenance.DerivedFromMemoryIds!.Count);
            Assert.Equal("Superseded", (await client.GetFromJsonAsync<AdminLearnedMemoryItemResponse>(path + $"/learned-memory/{sourceId}?scope=IdentityUser"))!.Status);
            var experience = (await s.GetRequiredService<IExperienceStore>().GetAsync(id, experienceId))!;
            Assert.Equal(ExperienceSourceKind.Consolidation, experience.SourceKind);
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            var tools = s.GetRequiredService<SessionToolExecutor>();
            var automation = new ToolExecutionAdmission(true, TriggerKind.ManualInvocation, AgentInstanceId: id);
            Assert.Equal(resultId, JsonSerializer.Deserialize<JsonElement>((await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: automation)).Text).GetProperty("memoryId").GetGuid());
            (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(settings.Revision, false))).EnsureSuccessStatusCode();
            Assert.Contains("PolicyDenied", (await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: automation)).Text);
            var call = new ModelToolCall("forget", ToolCatalog.MemoryForget, JsonSerializer.Serialize(new { memoryId = resultId }));
            var args = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson);
            var live = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: id);
            var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var foreign = call with { ArgumentsJson = JsonSerializer.Serialize(new { memoryId = sourceId }) };
            Assert.Contains("stale_approval", (await tools.ExecuteAsync(definition, Guid.Empty, foreign, 8000, approvalGrant: grant, admission: live)).Text);
            var result = await tools.ExecuteAsync(definition, Guid.Empty, call, 8000, approvalGrant: grant, admission: live);
            Assert.Contains("forgotten", result.Text);
            Assert.Contains("were not deleted", result.Text);
            var tombstone = (await client.GetFromJsonAsync<AdminLearnedMemoryItemResponse>(path + $"/learned-memory/{resultId}?scope=IdentityUser"))!;
            Assert.Equal("Deleted", tombstone.Status); Assert.Empty(tombstone.Content);
            Assert.Empty(await s.GetRequiredService<IStructuredMemoryStore>().ListActiveIdentityUserAsync(id, LocalUserProfile.Id));
            Assert.NotNull((await s.GetRequiredService<IExperienceStore>().GetAsync(id, experienceId))!.Content);
        }
    }
}

[CollectionDefinition("identity-maintenance-telemetry", DisableParallelization = true)]
public sealed class IdentityMaintenanceTelemetryCollection;
