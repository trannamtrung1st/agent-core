using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ExperienceSourceProvenanceTests
{
    [Theory(Timeout = 60000)]
    [InlineData(false, "ConversationResponse")]
    [InlineData(true, "BackgroundOutcome")]
    public async Task Review_projects_the_source_run_contract_independently_of_background_generation_and_after_reopen(bool background, string expected)
    {
        var db = Path.Combine(Path.GetTempPath(), $"experience-source-{Guid.NewGuid():N}.db");
        Guid instanceId, sourceId, experienceId;
        long revision;
        await using (var host = new ExperienceHost(db))
        {
            var services = host.Services;
            instanceId = (await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24)).InstanceId;
            var store = services.GetRequiredService<IExperienceStore>();
            await store.ConfigureAsync(instanceId, 0, true);
            AgentRun source;
            if (background)
            {
                var authoring = services.GetRequiredService<AdminAutomationAuthoringService>();
                var automation = await authoring.SaveAsync(instanceId, null, 0, true, "Source fixture", "synthetic-automation-attention",
                    new ScheduleTrigger(new FixedIntervalSchedule(60, DateTimeOffset.UtcNow.AddHours(1))), null, null,
                    executionTarget: AutomationExecutionTarget.Background);
                await authoring.RunNowAsync(instanceId, automation.AutomationId, automation.Revision);
                await AutomationJourneyTests.Intake(services);
                await services.ExecuteRunsAsync();
                source = Assert.Single(await services.AutomationRunsAsync(new(instanceId, LocalUserProfile.Id), automation.AutomationId));
            }
            else source = await services.CompleteSourceAsync(instanceId);
            sourceId = source.AgentRunId; revision = source.Revision;
            var experience = await services.GetRequiredService<ExperienceService>().RequestRunAsync(source);
            experienceId = experience.ExperienceId;
            var generation = (await services.GetRequiredService<IAgentRunStore>().GetAsync(source.Owner, experience.GenerationAgentRunId))!;
            Assert.Equal(AgentRunOutputContract.BackgroundOutcome, generation.Admission.OutputContract);
            Assert.NotEqual(source.AgentRunId, generation.AgentRunId);
            await services.ExecuteRunsAsync();
            Assert.NotNull((await store.GetAsync(instanceId, experience.ExperienceId))!.Content);
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var item = Assert.Single((await client.GetFromJsonAsync<ExperienceReviewResponse>($"/api/v2/admin/agent-instances/{instanceId}/experience"))!.Items);
            Assert.Equal(expected, item.SourceRunOutputContract);
            Assert.Equal(sourceId.ToString("D"), item.SourceId);
            Assert.Equal("AgentRun", item.SourceKind);
        }
        await using var reopened = new ExperienceHost(db);
        var response = await TestOwnerCapability.CreateOwnerClient(reopened)
            .GetFromJsonAsync<ExperienceReviewResponse>($"/api/v2/admin/agent-instances/{instanceId}/experience");
        var retained = Assert.Single(response!.Items);
        Assert.Equal(experienceId.ToString("D"), retained.ExperienceId);
        Assert.Equal(expected, retained.SourceRunOutputContract);
        Assert.Equal(revision, (await reopened.Services.GetRequiredService<IAgentRunStore>().GetAsync(new(instanceId, LocalUserProfile.Id), sourceId))!.Revision);
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_foreign_run_metadata_is_unknown_instead_of_inferred_from_generation(bool foreign)
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"experience-source-missing-{Guid.NewGuid():N}.db"));
        var services = host.Services;
        var admin = services.GetRequiredService<AdminAgentInstanceService>();
        var instance = await admin.CreateManagedAsync("general-assistant", 24);
        var generation = await services.CompleteSourceAsync(instance.InstanceId);
        var sourceId = foreign ? (await services.CompleteSourceAsync((await admin.CreateManagedAsync("general-assistant", 24)).InstanceId)).AgentRunId : Guid.NewGuid();
        var store = services.GetRequiredService<IExperienceStore>();
        await store.ConfigureAsync(instance.InstanceId, 0, true);
        await store.AdmitAsync(new(Guid.NewGuid(), instance.InstanceId, LocalUserProfile.Id, ExperienceSourceKind.AgentRun,
            sourceId, 1, generation.CreatedAtUtc, generation.DefinitionId, generation.DefinitionVersion, generation.AgentRunId,
            generation.PinnedModel, generation.CreatedAtUtc));
        var review = await TestOwnerCapability.CreateOwnerClient(host)
            .GetFromJsonAsync<ExperienceReviewResponse>($"/api/v2/admin/agent-instances/{instance.InstanceId}/experience");
        Assert.Null(Assert.Single(review!.Items).SourceRunOutputContract);
    }
}
