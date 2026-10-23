using System.Net;
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
using AgentCore.Domain.Conversation;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityReviewJourneyTests
{

    [Fact(Timeout = 90000)]
    public async Task Maintenance_reaches_instances_and_sessions_beyond_first_page_and_dedupes()
    {
        await using var host = new ExperienceHost(Database()); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var instances = s.GetRequiredService<IAgentInstanceStore>();
        Guid Key(int i) => Guid.Parse($"ffffffff-ffff-ffff-ffff-{i:000000000000}");
        for (var i = 1; i <= 101; i++) await instances.InsertAsync(instance with { InstanceId = Key(i) }, initialSkills: (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, instance.ActiveVersion))!.SkillList);
        var target = Key(101);
        await s.GetRequiredService<IExperienceStore>().ConfigureAsync(target, 0, true);
        var template = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(target, SessionMode.Text);
        var history = s.GetRequiredService<IMemoryStore>();
        for (var i = 1; i <= 101; i++)
        {
            var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.Assistant, "Completed meaningful audit", Guid.NewGuid(),
                EntryStatus.Completed, SessionMode.Text, 0, 26, DateTimeOffset.UtcNow);
            await history.SaveAsync(template with { SessionId = Key(i), Revision = 1, Status = SessionStatus.Attached,
                Entries = i == 101 ? [entry] : [], LastEntrySequence = i == 101 ? 1 : 0 }, 0);
        }
        await s.GetRequiredService<ExperienceService>().RequestSessionAsync(target, Key(101));
        await s.GetRequiredService<ExperienceService>().RequestSessionAsync(target, Key(101));
        var record = Assert.Single(await s.GetRequiredService<IExperienceStore>().ListAsync(target, 100));
        Assert.Equal(Key(101), record.SourceId);
        Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(target, LocalUserProfile.Id), 100));
    }

    private static string Database() => Path.Combine(Path.GetTempPath(), $"continuity-review-{Guid.NewGuid():N}.db");
    private sealed class OverrideDefinitions(IAgentDefinitionStore inner) : IAgentDefinitionStore
    {
        public TriggerPolicy? Policy { get; set; }
        public bool RemovePolicy { get; set; }
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        { var definition = await inner.GetAsync(id, version, cancellationToken); return definition is null ? null : RemovePolicy ? definition with { TriggerPolicy = null } : Policy is null ? definition : definition with { TriggerPolicy = Policy }; }
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    }
}
