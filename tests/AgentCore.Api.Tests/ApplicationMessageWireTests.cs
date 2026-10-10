using System.Text.Json;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using MessagePack;

namespace AgentCore.Api.Tests;

public sealed class ApplicationMessageWireTests
{
    [Fact]
    public void History_and_live_entry_use_application_message_once()
    {
        var entryId = Guid.Parse("019944af-00ee-7000-8000-0000000000f1");
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000f2");
        const string text = "Still checking the order";
        var createdAt = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var historyEntry = new PublicHistoryEntry(
            entryId,
            4,
            null,
            ConversationRole.ApplicationMessage,
            text,
            responseId,
            EntryStatus.Completed,
            0,
            text.Length,
            SessionMode.Text,
            createdAt,
            []);
        var stored = new ConversationEntry(
            entryId,
            4,
            null,
            ConversationRole.ApplicationMessage,
            text,
            responseId,
            EntryStatus.Completed,
            SessionMode.Text,
            0,
            text.Length,
            createdAt,
            ApplicationMessageEffectKey: "v1:effect");
        var http = HttpMapping.ToHistoryItem(stored);
        var live = SessionEventMapper.Map(
            new SessionOutput(Context(), responseId, new HistoryEntryUpsertOutput(historyEntry)),
            Guid.NewGuid(),
            8);
        var ready = SessionEventMapper.Map(
            new SessionOutput(Context(), null, new ReadyOutput(Ready(historyEntry))),
            Guid.NewGuid(),
            9);
        var reattached = SessionEventMapper.Map(
            new SessionOutput(Context(), null, new ReadyOutput(Ready(historyEntry))),
            Guid.NewGuid(),
            10);

        Assert.Equal("session.entry.upsert", live.Type);
        Assert.Equal(responseId.ToString(), live.ResponseId);
        Assert.Equal("applicationMessage", live.Payload["role"]);
        Assert.Equal(text, live.Payload["text"]);
        Assert.Equal(entryId.ToString(), live.Payload["entryId"]);
        Assert.Equal(0, live.Payload["heardTextEndExclusive"]);
        Assert.Equal(text.Length, live.Payload["receivedTextEndExclusive"]);
        Assert.Null(live.Payload["speechText"]);
        Assert.False(live.Payload.ContainsKey("destination"));
        Assert.False(live.Payload.ContainsKey("applicationMessageEffectKey"));
        Assert.Equal(http.Role, live.Payload["role"]);
        Assert.Equal(http.Text, live.Payload["text"]);
        Assert.Equal(http.EntryId, live.Payload["entryId"]);

        var firstHistory = History(ready);
        var secondHistory = History(reattached);
        var first = Assert.Single(firstHistory);
        var second = Assert.Single(secondHistory);
        Assert.Equal(entryId.ToString(), first["entryId"]);
        Assert.Equal(first["entryId"], second["entryId"]);
        Assert.Equal("applicationMessage", first["role"]);
        Assert.Equal(live.Payload["role"], first["role"]);
        Assert.Equal(live.Payload["text"], first["text"]);
        Assert.Equal(live.Payload["entryId"], first["entryId"]);
        Assert.Null(first["speechText"]);
    }

    [Fact]
    public void MessagePack_and_json_payloads_keep_the_application_message_role()
    {
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000f2");
        var mapped = SessionEventMapper.Map(
            new SessionOutput(
                Context(),
                responseId,
                new HistoryEntryUpsertOutput(Entry(responseId))),
            Guid.NewGuid(),
            3);

        var roundTripped = MessagePackSerializer.Deserialize<ServerEvent>(
            MessagePackSerializer.Serialize(mapped));
        Assert.Equal("session.entry.upsert", roundTripped.Type);
        Assert.Equal(responseId.ToString(), roundTripped.ResponseId);
        Assert.Equal("applicationMessage", Convert.ToString(roundTripped.Payload["role"]));
        Assert.Equal("Still checking the order", Convert.ToString(roundTripped.Payload["text"]));
        Assert.Equal("0", Convert.ToString(roundTripped.Payload["heardTextEndExclusive"]));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(mapped.Payload));
        Assert.Equal("applicationMessage", json.RootElement.GetProperty("role").GetString());
        Assert.Equal("Still checking the order", json.RootElement.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("speechText").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("destination", out _));
        Assert.Equal(
            Convert.ToString(roundTripped.Payload["role"]),
            json.RootElement.GetProperty("role").GetString());
        Assert.Equal(
            Convert.ToString(roundTripped.Payload["entryId"]),
            json.RootElement.GetProperty("entryId").GetString());
    }

    [Fact]
    public void Client_text_payload_cannot_set_role_or_destination()
    {
        var names = typeof(UserTextPayload).GetProperties().Select(property => property.Name).Order().ToArray();
        Assert.Equal(["AttachmentIds", "Behavior", "Parts", "Text"], names);

        var payload = JsonSerializer.Deserialize<UserTextPayload>(
            """{"text":"hello","role":"applicationMessage","destination":"other-session","sessionId":"elsewhere"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.Equal("hello", payload.Text);
        Assert.Null(payload.AttachmentIds);
        Assert.Null(payload.Behavior);

        var packed = MessagePackSerializer.Deserialize<UserTextPayload>(
            MessagePackSerializer.Serialize(new Dictionary<string, object?>
            {
                ["text"] = "hello",
                ["role"] = "assistant",
                ["destination"] = "other-session"
            }));
        Assert.Equal("hello", packed.Text);
        Assert.Null(packed.AttachmentIds);
        Assert.Null(packed.Behavior);
    }

    private static PublicHistoryEntry Entry(Guid responseId) =>
        new(
            Guid.Parse("019944af-00ee-7000-8000-0000000000f1"),
            4,
            null,
            ConversationRole.ApplicationMessage,
            "Still checking the order",
            responseId,
            EntryStatus.Completed,
            0,
            "Still checking the order".Length,
            SessionMode.Text,
            DateTimeOffset.Parse("2026-09-30T12:00:00Z"),
            []);

    private static SessionReadyProjection Ready(PublicHistoryEntry entry) =>
        new(
            SessionMode.Text,
            null,
            SessionStatus.Attached,
            new PublicAgentDescriptor("examiner", 1, "Examiner", "role", "desc", false),
            null,
            null,
            new RecognitionCapabilities(false, false, false, false),
            new SynthesisCapabilities(false, false, false, false, false, []),
            "none",
            entry.Sequence,
            [entry],
            null,
            "browser",
            "browser");

    private static Dictionary<string, object?>[] History(ServerEvent ready)
    {
        var history = Assert.IsAssignableFrom<IEnumerable<object>>(ready.Payload["history"]);
        return history.Select(item => Assert.IsAssignableFrom<IDictionary<string, object?>>(item))
            .Select(item => item.ToDictionary(pair => pair.Key, pair => pair.Value))
            .ToArray();
    }

    private static EventContext Context() =>
        new(
            Guid.Parse("019944af-00ee-7000-8000-0000000000aa"),
            Guid.Parse("019944af-00ee-7000-8000-0000000000ab"),
            Guid.Parse("019944af-00ee-7000-8000-0000000000ac"),
            DateTimeOffset.Parse("2026-09-30T12:00:01Z"),
            Guid.Parse("019944af-00ee-7000-8000-0000000000ad"),
            null);
}
