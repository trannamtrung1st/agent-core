using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    [Fact]
    public async Task Tenant_continuity_historical_projection_keeps_received_or_heard_prefixes_secrets_and_whole_entry_budget_boundaries()
    {
        var at = DateTimeOffset.UtcNow; var text = "Delivered prefix. UNDELIVERED_PRIVATE_TAIL";
        ConversationEntry Entry(long sequence, SessionMode mode, EntryStatus status = EntryStatus.Completed) => new(Guid.NewGuid(), sequence, null, ConversationRole.Assistant,
            text, Guid.NewGuid(), status, mode, 8, 17, at, Envelope: new(text, null, [], ResponseSpeechMode.None));
        var page = TenantContinuityRules.Project(Guid.NewGuid(), 1, 4, "Safe title", "Safe summary", at,
            [Entry(1, SessionMode.Text), Entry(2, SessionMode.Voice, EntryStatus.Interrupted), Entry(3, SessionMode.Text, EntryStatus.Streaming), Entry(4, SessionMode.Text) with { Text = "sk-abcdef01234567890123456789", Envelope = null, ReceivedTextEndExclusive = 45 }], 0, 20, false);
        Assert.Equal(3, page.Entries.Count); Assert.Equal(text[..17], page.Entries[0].Text); Assert.Equal(text[..8], page.Entries[1].Text); Assert.DoesNotContain("UNDELIVERED_PRIVATE_TAIL", JsonSerializer.Serialize(page));
        Assert.Contains("sensitive historical content omitted", page.Entries[2].Text);
        await using var f = await Fixture.Create(false); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source)); var current = await MemoryClaim(f, owner);
        var observed = await ContinuityServices(f).Reads.PrepareAsync(new(owner.Tenant, current.Frame.RunId), current.Frame.Generation, ToolCatalog.ContinuityGet, HistoryCall(source.Frame.SessionId).ArgumentsJson);
        var entries = Enumerable.Range(1, 20).Select(n => new TenantHistoricalEntry(Guid.NewGuid(), n, "User", "Completed", new string('界', 700), false)).ToArray();
        var large = observed with { Sessions = [observed.Sessions[0] with { Entries = entries, ThroughEntrySequence = 20 }] };
        var body = TenantContinuityReadService.Serialize(large, 6000, at); Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 6000); using var json = JsonDocument.Parse(body); var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        var returned = item.GetProperty("entries").EnumerateArray().ToArray(); Assert.InRange(returned.Length, 1, 19); Assert.All(returned, e => Assert.Equal(new string('界', 700), e.GetProperty("text").GetString())); Assert.True(item.GetProperty("hasMore").GetBoolean()); Assert.Equal(returned[^1].GetProperty("sequence").GetInt64(), item.GetProperty("nextAfter").GetInt64());
        var tooSmall = TenantContinuityReadService.Serialize(large, 200, at); Assert.DoesNotContain("界", tooSmall); Assert.Contains("finish_required", tooSmall);
    }
}
