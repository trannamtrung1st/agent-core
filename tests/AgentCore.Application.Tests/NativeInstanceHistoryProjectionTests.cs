using AgentCore.Application.Admin;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Identity;

namespace AgentCore.Application.Tests;

public sealed class NativeInstanceHistoryProjectionTests
{
    [Fact]
    public async Task ListEvents_returns_a_legacy_managed_instance_created_summary()
    {
        var events = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System));
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-0000000000c4");
        var append = AdminEventFactory.ManagedInstanceCreated(
            Guid.Parse("019944af-00d1-7000-8000-0000000000c5"),
            DateTimeOffset.Parse("2026-09-28T00:00:00Z"),
            "examiner",
            instanceId,
            1);
        Assert.DoesNotContain("personaSource", append.SummaryJson, StringComparison.Ordinal);
        await events.AppendAsync(append, CancellationToken.None);

        var listed = await events.ListAsync(
            new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
            CancellationToken.None);

        Assert.Equal(append.SummaryJson, Assert.Single(listed).SummaryJson);
    }

}
