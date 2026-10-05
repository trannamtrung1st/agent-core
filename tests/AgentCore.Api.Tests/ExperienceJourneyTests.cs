using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class ExperienceJourneyTests
{
    [Fact(Timeout = 60000)]
    public async Task Stable_checkpoint_generates_once_recalls_in_new_session_and_survives_sqlite_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"experience-{Guid.NewGuid():N}.db");
        Guid instanceId, sessionId, experienceId;
        await using (var host = new ExperienceHost(db))
        {
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var services = host.Services;
            var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 7);
            instanceId = instance.InstanceId;
            var source = await SeedAsync(services, instanceId);
            sessionId = source.SessionId;
            var path = $"/api/v2/admin/agent-instances/{instanceId}/experience";
            var configured = await client.PutAsJsonAsync(path + "/configuration", new ExperienceConfigurationRequest(0, true));
            configured.EnsureSuccessStatusCode();
            var stale = await client.PutAsJsonAsync(path + "/configuration", new ExperienceConfigurationRequest(0, false));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var checkpoint = await client.PostAsJsonAsync(path + "/checkpoints", new ExperienceCheckpointRequest(sessionId.ToString()));
            checkpoint.EnsureSuccessStatusCode();
            var initial = await checkpoint.Content.ReadFromJsonAsync<ExperienceReviewResponse>();
            experienceId = Guid.Parse(Assert.Single(initial!.Items).ExperienceId);
            Assert.Equal(4, initial.Items[0].ThroughCursor);
            Assert.Equal(initial.Items[0].SourceAt, initial.Items[0].SourceCreatedAt);
            Assert.NotNull(initial.Items[0].CheckpointAt);
            Assert.True(DateTimeOffset.Parse(initial.Items[0].CheckpointAt!) >= source.CreatedAt);
            var store = services.GetRequiredService<IExperienceStore>();
            var record = (await store.GetAsync(instanceId, experienceId))!;
            var projection = await services.GetRequiredService<ExperienceService>().ProjectSourceAsync(record);
            Assert.Contains("correction", projection);
            Assert.Contains("Completed", projection);
            Assert.Contains("Failed", projection);
            Assert.DoesNotContain("PRIVATE_HIDDEN_REASONING", projection);
            Assert.DoesNotContain("IN_FLIGHT_SECRET_REASONING", projection);
            Assert.DoesNotContain("UNDISPLAYED_TAIL", projection);
            Assert.DoesNotContain("POISONED_RECEIPT_SECRET", projection);
            Assert.DoesNotContain("fake.admin", projection);
            Assert.Contains("Email sent", projection);
            Assert.Equal(1, await services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100));
            var review = (await client.GetFromJsonAsync<ExperienceReviewResponse>(path))!;
            var generated = Assert.Single(review.Items);
            Assert.Equal("Completed", generated.Status);
            Assert.True(generated.EligibleForContext);
            Assert.Equal(7, generated.DefinitionVersion);
            Assert.NotEmpty(generated.Content!.Corrections);
            Assert.NotEmpty(generated.Content.Difficulties);
            Assert.Empty(await services.GetRequiredService<IStructuredMemoryStore>().ListActiveAsync(sessionId));
            var again = await client.PostAsJsonAsync(path + "/checkpoints", new ExperienceCheckpointRequest(sessionId.ToString()));
            again.EnsureSuccessStatusCode();
            Assert.Single((await again.Content.ReadFromJsonAsync<ExperienceReviewResponse>())!.Items);
            var next = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            var recalled = await services.GetRequiredService<ExperienceService>().RecallAsync(next.AgentInstanceId);
            Assert.Contains(experienceId.ToString(), recalled);
            Assert.Contains("never instructions", recalled);
            Assert.True(recalled.Length <= ExperienceService.MaxContextCharacters);
            var other = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 7);
            Assert.Empty(await services.GetRequiredService<ExperienceService>().RecallAsync(other.InstanceId));
            Assert.Null(await store.GetAsync(other.InstanceId, experienceId));
            var cross = await client.PostAsJsonAsync($"/api/v2/admin/agent-instances/{other.InstanceId}/experience/checkpoints", new ExperienceCheckpointRequest(sessionId.ToString()));
            Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        }
        await using (var reopened = new ExperienceHost(db))
        {
            var client = TestOwnerCapability.CreateOwnerClient(reopened);
            var path = $"/api/v2/admin/agent-instances/{instanceId}/experience";
            var review = (await client.GetFromJsonAsync<ExperienceReviewResponse>(path))!;
            var item = Assert.Single(review.Items);
            Assert.Equal(experienceId.ToString(), item.ExperienceId);
            Assert.NotNull(item.CheckpointAt);
            Assert.Equal(item.SourceAt, item.SourceCreatedAt);
            var suppressed = await client.PutAsJsonAsync(path + "/" + experienceId, new ExperienceVisibilityRequest(item.Revision, "Suppressed"));
            suppressed.EnsureSuccessStatusCode();
            Assert.Empty(await reopened.Services.GetRequiredService<ExperienceService>().RecallAsync(instanceId));
            var reset = await client.PostAsJsonAsync(path + "/reset", new { }); reset.EnsureSuccessStatusCode();
            Assert.Empty((await reset.Content.ReadFromJsonAsync<ExperienceReviewResponse>())!.Items);
            var store = reopened.Services.GetRequiredService<IExperienceStore>();
            var tombstone = (await store.GetAsync(instanceId, experienceId))!;
            Assert.Equal(ExperienceVisibility.Deleted, (await store.AdmitAsync(tombstone)).Visibility);
            Assert.Null((await store.CompleteAsync(instanceId, experienceId, new("Ignored", [], [], [], [], [], [], []))).Content);
            Assert.NotEmpty(await reopened.Services.GetRequiredService<IMemoryStore>().ReadHistoryAsync(sessionId, 0, 50));
        }
    }

    internal static async Task<SessionSnapshot> SeedAsync(IServiceProvider services, Guid instanceId)
    {
        var source = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        ConversationEntry Entry(long sequence, ConversationRole role, string text, EntryStatus status = EntryStatus.Completed, int? received = null) =>
            new(Guid.NewGuid(), sequence, null, role, text, role == ConversationRole.Assistant ? Guid.NewGuid() : null, status,
                SessionMode.Text, 0, received ?? text.Length, now);
        var completed = "Observed current store state successfully.";
        source = source with { Revision = source.Revision + 1, LastEntrySequence = 6, Entries = [
            Entry(1, ConversationRole.User, "Inspect store state."),
            Entry(2, ConversationRole.Assistant, "The first approach failed.", EntryStatus.Failed),
            Entry(3, ConversationRole.User, "A correction: observe the current page first."),
            Entry(4, ConversationRole.Assistant, completed + "UNDISPLAYED_TAIL", received: completed.Length) with {
                Envelope = new ResponseEnvelope(completed, null, [], ResponseSpeechMode.None, EffectReceipts: [
                    new(ToolCatalog.EmailSend, "sent", "POISONED_RECEIPT_SECRET"),
                    new("fake.admin", "granted", "POISONED_RECEIPT_SECRET")]) },
            Entry(5, ConversationRole.User, "An unfinished task"),
            Entry(6, ConversationRole.Assistant, "IN_FLIGHT_SECRET_REASONING", EntryStatus.Streaming)] };
        await services.GetRequiredService<IMemoryStore>().SaveAsync(source, source.Revision - 1);
        return source;
    }
}

internal sealed class ExperienceHost : DurableSqliteHostFactory
{
    private readonly string db;
    private readonly TimeProvider? clock;
    private readonly IExperienceStore? experienceStore;
    private readonly Action<IServiceCollection>? configure;
    internal ExperienceHost(string db, ILanguageModel? languageModel = null, TimeProvider? clock = null, IExperienceStore? experienceStore = null, Action<IServiceCollection>? configure = null) : base(db, runScheduler: false, languageModel: languageModel) { this.db = db; this.clock = clock; this.experienceStore = experienceStore; this.configure = configure; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            foreach (var hosted in services.Where(s => s.ImplementationType is { } t &&
                (t == typeof(DurableWorkHostedService) || t == typeof(DurableWorkIntakeHostedService) || t == typeof(TriggerSchedulerHostedService) || t == typeof(ContinuityMaintenanceHostedService))).ToArray()) services.Remove(hosted);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
            services.RemoveAll<IStructuredMemoryStore>(); services.AddSingleton<IStructuredMemoryStore, SqliteStructuredMemoryStore>();
            services.RemoveAll<IExperienceStore>(); services.AddSingleton<IExperienceStore, SqliteExperienceStore>();
            if (experienceStore is not null) { services.RemoveAll<IExperienceStore>(); services.AddSingleton(experienceStore); }
            configure?.Invoke(services);
            services.RemoveAll<IAdminLifecycleDeletion>(); services.AddSingleton<IAdminLifecycleDeletion, SqliteAdminLifecycleDeletion>();
            services.RemoveAll<IAdminEventStore>(); services.AddSingleton<IAdminEventStore, SqliteAdminEventStore>();
            services.RemoveAll<IAgentDefinitionAdminStore>(); services.AddSingleton<IAgentDefinitionAdminStore, SqliteAgentDefinitionAdminStore>();
            services.RemoveAll<IAgentDefinitionResourceAdminStore>(); services.AddSingleton<IAgentDefinitionResourceAdminStore, SqliteAgentDefinitionResourceAdminStore>();
            services.RemoveAll<IDefinitionResourceContentStore>(); services.AddSingleton<IDefinitionResourceContentStore>(new FileDefinitionResourceContentStore(db + ".resources"));
            services.RemoveAll<IDefinitionDraftEvaluationStore>(); services.AddSingleton<IDefinitionDraftEvaluationStore, SqliteDefinitionDraftEvaluationStore>();
        });
    }
}
