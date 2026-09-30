using System.Reflection;
using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class ResponseEnvelopeJsonTests
{
    [Fact]
    public void Domain_public_types_have_no_openai_schema_field_names()
    {
        foreach (var type in typeof(ResponseEnvelope).Assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public))
            {
                Assert.False(
                    string.Equals(member.Name, "ResponseFormat", StringComparison.Ordinal)
                    || string.Equals(member.Name, "JsonSchema", StringComparison.Ordinal)
                    || member.Name.Contains("json_schema", StringComparison.Ordinal)
                    || member.Name.Contains("response_format", StringComparison.Ordinal),
                    type.FullName + "." + member.Name);
            }
        }
    }

    [Fact]
    public void New_envelopes_round_trip_speech_mode()
    {
        var original = ResponseEnvelope.Create(
            "Hello",
            new ResponseSpeech(ResponseSpeechMode.Custom, "Spoken"),
            [new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "Hi", null, null, true)]);
        var json = ResponseEnvelopeJson.Serialize(original);
        Assert.Contains("\"mode\":\"custom\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("speechText", json, StringComparison.Ordinal);
        var restored = ResponseEnvelopeJson.Deserialize(json);
        Assert.Equal(original.DisplayText, restored!.DisplayText);
        Assert.Equal(ResponseSpeechMode.Custom, restored.SpeechMode);
        Assert.Equal("Spoken", restored.SpeechText);
        Assert.True(Assert.Single(restored.Blocks).DisplayDelivered);
        Assert.Null(restored.MemoryReceipts);
    }

    [Fact]
    public void Memory_receipts_round_trip_apart_from_display_and_speech()
    {
        var original = new ResponseEnvelope(
            "Got it.",
            "Spoken line.",
            [],
            ResponseSpeechMode.Custom,
            [new MemoryReceipt("stored", "upsert", "userExplicit", "project codename", ["session", "identityUser"], MemoryReceipt.Indicator)]);
        var json = ResponseEnvelopeJson.Serialize(original);
        Assert.Contains("\"memoryReceipts\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scopes\":[\"session\",\"identityUser\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory saved", json, StringComparison.Ordinal);
        Assert.Contains("\"displayText\":\"Got it.\"", json, StringComparison.Ordinal);
        var restored = ResponseEnvelopeJson.Deserialize(json);
        Assert.Equal("Got it.", restored!.DisplayText);
        Assert.Equal("Spoken line.", restored.SpeechText);
        var receipt = Assert.Single(restored.MemoryReceipts!);
        Assert.Equal("project codename", receipt.Subject);
        Assert.Equal(MemoryReceipt.Indicator, receipt.Presentation);
        Assert.Equal(["session", "identityUser"], receipt.Scopes);
    }

    [Fact]
    public void Legacy_memory_receipt_scope_deserializes_as_scopes()
    {
        var restored = ResponseEnvelopeJson.Deserialize(
            """
            {"displayText":"Hi","speech":{"mode":"same"},"blocks":[],"memoryReceipts":[{"outcome":"stored","operation":"upsert","source":"userExplicit","subject":"token","scope":"session","presentation":"indicator"}]}
            """);
        var receipt = Assert.Single(restored!.MemoryReceipts!);
        Assert.Equal(["session"], receipt.Scopes);
    }

    [Fact]
    public void Same_mode_derived_playback_coordinate_round_trips()
    {
        var display = "Visit [OpenAI](https://openai.com) today.";
        var spoken = "Visit OpenAI today.";
        var original = new ResponseEnvelope(display, spoken, [], ResponseSpeechMode.Same);
        var json = ResponseEnvelopeJson.Serialize(original);
        Assert.Contains("\"mode\":\"same\"", json, StringComparison.Ordinal);
        Assert.Contains(spoken, json, StringComparison.Ordinal);
        var restored = ResponseEnvelopeJson.Deserialize(json);
        Assert.Equal(ResponseSpeechMode.Same, restored!.SpeechMode);
        Assert.Equal(spoken, restored.SpeechText);
    }

    [Fact]
    public void Equivalent_custom_speech_normalizes_to_same_on_persist()
    {
        var envelope = ResponseEnvelope.Create(
            "Hello",
            new ResponseSpeech(ResponseSpeechMode.Custom, "Hello"),
            []);
        Assert.Equal(ResponseSpeechMode.Custom, envelope.SpeechMode);
        var json = ResponseEnvelopeJson.Serialize(envelope);
        Assert.Contains("\"mode\":\"same\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", json, StringComparison.Ordinal);
        var restored = ResponseEnvelopeJson.Deserialize(json);
        Assert.Equal(ResponseSpeechMode.Same, restored!.SpeechMode);
        Assert.Null(restored.SpeechText);
    }

    [Fact]
    public void None_and_same_round_trip_without_speech_text()
    {
        var none = ResponseEnvelopeJson.Deserialize(ResponseEnvelopeJson.Serialize(
            ResponseEnvelope.Create("Chart", new ResponseSpeech(ResponseSpeechMode.None, null), [])));
        Assert.Equal(ResponseSpeechMode.None, none!.SpeechMode);
        Assert.Null(none.SpeechText);
        var same = ResponseEnvelopeJson.Deserialize(ResponseEnvelopeJson.Serialize(
            ResponseEnvelope.Create("Hello", new ResponseSpeech(ResponseSpeechMode.Same, null), [])));
        Assert.Equal(ResponseSpeechMode.Same, same!.SpeechMode);
        Assert.Null(same.SpeechText);
    }

    [Fact]
    public void Non_empty_legacy_speech_text_deserializes_as_custom()
    {
        var restored = ResponseEnvelopeJson.Deserialize(
            """{"displayText":"Hello","speechText":"Spoken","blocks":[]}""");
        Assert.Equal("Hello", restored!.DisplayText);
        Assert.Equal(ResponseSpeechMode.Custom, restored.SpeechMode);
        Assert.Equal("Spoken", restored.SpeechText);
    }

    [Fact]
    public void Null_or_empty_legacy_speech_text_deserializes_as_same()
    {
        var missing = ResponseEnvelopeJson.Deserialize("""{"displayText":"Hello","blocks":[]}""");
        Assert.Equal(ResponseSpeechMode.Same, missing!.SpeechMode);
        Assert.Null(missing.SpeechText);
        var explicitNull = ResponseEnvelopeJson.Deserialize(
            """{"displayText":"Hello","speechText":null,"blocks":[]}""");
        Assert.Equal(ResponseSpeechMode.Same, explicitNull!.SpeechMode);
        var empty = ResponseEnvelopeJson.Deserialize(
            """{"displayText":"Hello","speechText":"","blocks":[]}""");
        Assert.Equal(ResponseSpeechMode.Same, empty!.SpeechMode);
        Assert.Null(empty.SpeechText);
    }
}
