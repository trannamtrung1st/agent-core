using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

internal static class ExperienceTestSessions
{
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
