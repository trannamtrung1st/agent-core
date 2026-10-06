using System.Net;
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
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Work;
using AgentCore.Application.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class IdentityMaintenanceJourneyTests
{
    [Theory(Timeout = 90000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Protected_Thought_approval_survives_restart_and_late_delete_or_opt_out_wins(bool optOut)
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-approval-{Guid.NewGuid():N}.db");
        Guid id, workId, sourceId;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
            id = instance.InstanceId;
            await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 9))!;
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
            var thoughts = s.GetRequiredService<ThoughtRegistrationService>();
            var r = await thoughts.SaveAsync(id, null, 0, true, 3600, "synthetic-maintain-memory", null, null);
            await thoughts.RunNowAsync(id, r.RegistrationId, r.Revision);
            await ThoughtJourneyTests.Intake(s);
            await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
            var work = Assert.Single(await s.GetRequiredService<IWorkItemStore>().ListAsync(new(id, LocalUserProfile.Id), 100));
            workId = work.WorkItemId;
            Assert.Equal(WorkItemStatus.WaitingForApproval, work.Status);
            Assert.Equal(ToolCatalog.MemoryConsolidate, work.Approval!.ToolName);
            Assert.Equal(3, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
        }
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var store = s.GetRequiredService<IWorkItemStore>();
            var work = (await store.GetAsync(new(id, LocalUserProfile.Id), workId))!;
            var a = work.Approval!;
            Assert.Equal(WorkItemStatus.WaitingForApproval, work.Status);
            if (optOut) await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 1, false);
            else await s.GetRequiredService<IStructuredMemoryService>().DeleteIdentityUserAsync(new(id, LocalUserProfile.Id), sourceId, true);
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var url = $"/api/v2/admin/agent-instances/{id}/work-items/{workId}/approvals/{a.ApprovalId}/approve";
            (await client.PostAsJsonAsync(url, new DecideWorkApprovalRequest(work.Revision, a.Revision, a.ActionHash))).EnsureSuccessStatusCode();
            await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
            Assert.Equal(optOut ? 3 : 2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
            Assert.DoesNotContain(await s.GetRequiredService<IStructuredMemoryStore>().ListActiveIdentityUserAsync(id, LocalUserProfile.Id), m => m.Provenance.DerivedFromMemoryIds is { Count: > 0 });
            if (!optOut) Assert.Equal(MemoryItemStatus.Deleted, (await s.GetRequiredService<IStructuredMemoryStore>().FindIdentityUserAsync(id, LocalUserProfile.Id, sourceId))!.Status);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Contradictory_inferred_memory_Thought_noops_without_alerts_or_mutation()
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-noop-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var id = instance.InstanceId;
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 9))!;
        var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var memory = s.GetRequiredService<IStructuredMemoryService>();
        var profile = await s.GetRequiredService<IMemoryStore>().LoadProfileAsync(LocalUserProfile.Id);
        var admission = SessionMemoryPrompt.CreateAdmissionContext("agent_inferred", definition, profile, []);
        foreach (var (subject, content) in new[] { ("Frontend TypeScript", "Prefer TypeScript for frontend examples."), ("Frontend Python", "Prefer Python for frontend examples.") })
        {
            var source = await memory.WriteAsync(new(session.SessionId), new(MemoryKind.Preference, subject, content, []), admission);
            await memory.PromoteToIdentityUserAsync(new(session.SessionId), source.MemoryId, new(id, LocalUserProfile.Id), true, admission);
        }
        var thoughts = s.GetRequiredService<ThoughtRegistrationService>();
        var r = await thoughts.SaveAsync(id, null, 0, true, 3600, "synthetic-maintain-memory", null, null);
        await thoughts.RunNowAsync(id, r.RegistrationId, r.Revision); await ThoughtJourneyTests.Intake(s);
        await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
        var store = s.GetRequiredService<IWorkItemStore>();
        var work = Assert.Single(await store.ListAsync(new(id, LocalUserProfile.Id), 100));
        Assert.Equal(WorkItemStatus.Completed, work.Status);
        Assert.Equal("NoAction", ThoughtCompletion.Outcome(work.Result!.Text));
        Assert.False(work.Result.AttentionRequired);
        Assert.Empty(await store.ListAttentionAlertKeysAsync(work.WorkItemId));
        Assert.Equal(2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
    }

    [Fact(Timeout = 60000)]
    public async Task Invalid_arguments_sensitive_content_owner_overrides_and_execution_origins_never_mutate()
    {
        var db = Path.Combine(Path.GetTempPath(), $"p910-validation-{Guid.NewGuid():N}.db");
        await using var host = new ExperienceHost(db);
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var id = instance.InstanceId;
        await s.GetRequiredService<IExperienceStore>().ConfigureMaintenanceAsync(id, 0, true);
        var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 9))!;
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
        var thought = new ToolExecutionAdmission(true, TriggerKind.ThoughtActivation, AgentInstanceId: id);
        object Payload(string content = "Prefer TypeScript for frontend examples.") => new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend examples", content };
        var invalid = new[] { "[]", JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Fact", subject = "Frontend examples", content = "Wrong kind" }),
            JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend examples", content = "Untrusted", ownerInstanceId = Guid.NewGuid() }),
            JsonSerializer.Serialize(Payload("sk-abcdefghijklmnopqrst")), JsonSerializer.Serialize(Payload(new string('x', 2001))) };
        foreach (var args in invalid)
        {
            var result = await tools.ExecuteAsync(definition, Guid.Empty, new("invalid", ToolCatalog.MemoryConsolidate, args), 8000, admission: thought);
            Assert.Contains("error", result.Text);
            Assert.Equal(2, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
        }
        var valid = new ModelToolCall("valid", ToolCatalog.MemoryConsolidate, JsonSerializer.Serialize(Payload()));
        foreach (var denied in new[] { thought with { SupportsTools = false }, thought with { TriggerKind = TriggerKind.ScheduledOccurrence } })
            Assert.Contains("forbidden", (await tools.ExecuteAsync(definition, Guid.Empty, valid, 8000, admission: denied)).Text);
        // Schema constraints supplied as properties receive actionable validation, never relaxed acceptance.
        var extra = valid with { ArgumentsJson = JsonSerializer.Serialize(new { sourceMemoryIds = sources, kind = "Preference", subject = "Frontend examples", content = "Safe", minItems = 2 }) };
        var argsElement = JsonSerializer.Deserialize<JsonElement>(extra.ArgumentsJson);
        Assert.Equal(ToolPolicyDecision.Allow, await tools.EvaluateExecutionPolicyAsync(definition, Guid.Empty, extra, argsElement, thought, default));
        Assert.Contains("Unsupported maintenance argument", (await tools.ExecuteAsync(definition, Guid.Empty, extra, 8000, admission: thought)).Text);
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
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
            id = instance.InstanceId;
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{id}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(path + "/maintenance")).StatusCode);
            var settings = await client.GetFromJsonAsync<IdentityMaintenanceSettings>(path + "/maintenance");
            Assert.False(settings!.AllowAgentConsolidation);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(-1, true))).StatusCode);
            (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(0, true))).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(0, false))).StatusCode);
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, 9))!;
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
            var thought = new ToolExecutionAdmission(true, TriggerKind.ThoughtActivation, AgentInstanceId: id);
            memoryCall = new("maintenance-memory", ToolCatalog.MemoryConsolidate, JsonSerializer.Serialize(new { sourceMemoryIds = stored.Select(m => m.MemoryId), kind = "Preference", subject = "Frontend language", content = "Prefer TypeScript for frontend examples." }));
            var args = JsonSerializer.Deserialize<JsonElement>(memoryCall.ArgumentsJson);
            Assert.Equal(ToolPolicyDecision.Allow, await tools.EvaluateExecutionPolicyAsync(definition, Guid.Empty, memoryCall, args, thought, default));
            var response = await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: thought);
            var data = JsonSerializer.Deserialize<JsonElement>(response.Text);
            Assert.Equal("consolidated", data.GetProperty("status").GetString());
            resultId = data.GetProperty("memoryId").GetGuid();
            Assert.Equal(resultId, JsonSerializer.Deserialize<JsonElement>((await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: thought)).Text).GetProperty("memoryId").GetGuid());
            Assert.Equal(1, await s.GetRequiredService<IStructuredMemoryStore>().CountActiveIdentityUserAsync(id, LocalUserProfile.Id));
            var historical = (await client.GetFromJsonAsync<AdminLearnedMemoryItemResponse>(path + $"/learned-memory/{sourceId}?scope=IdentityUser"))!;
            Assert.Equal("Superseded", historical.Status);
            Assert.Contains("TypeScript", historical.Content);
            var active = (await client.GetFromJsonAsync<AdminLearnedMemoryListResponse>(path + "/learned-memory?scope=IdentityUser"))!;
            Assert.Equal(3, Assert.Single(active.Items).Provenance.DerivedFromMemoryIds!.Count);
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
            await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
            var call = new ModelToolCall("maintenance-experience", ToolCatalog.ExperienceConsolidate, JsonSerializer.Serialize(new {
                sourceExperienceIds = records.Select(r => r.ExperienceId), goal = "Review repeated browser work", attempts = new[] { "Observed current page" },
                decisions = Array.Empty<string>(), outcomes = new[] { "Observed successful retry" }, corrections = new[] { "Check page state first" },
                unresolved = Array.Empty<string>(), difficulties = new[] { "Earlier approaches failed" }, lessons = new[] { "Observe current page state before browser actions; verify each outcome." } }));
            var output = await tools.ExecuteAsync(definition, Guid.Empty, call, 8000, admission: thought);
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
            Assert.Contains("ApprovalRequired", (await tools.ExecuteAsync(definition, Guid.Empty, forbiddenForget, 8000, admission: thought)).Text);
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
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 9))!;
            var tools = s.GetRequiredService<SessionToolExecutor>();
            var thought = new ToolExecutionAdmission(true, TriggerKind.ThoughtActivation, AgentInstanceId: id);
            Assert.Equal(resultId, JsonSerializer.Deserialize<JsonElement>((await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: thought)).Text).GetProperty("memoryId").GetGuid());
            (await client.PutAsJsonAsync(path + "/maintenance", new IdentityMaintenanceConfigurationRequest(settings.Revision, false))).EnsureSuccessStatusCode();
            Assert.Contains("PolicyDenied", (await tools.ExecuteAsync(definition, Guid.Empty, memoryCall, 8000, admission: thought)).Text);
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
