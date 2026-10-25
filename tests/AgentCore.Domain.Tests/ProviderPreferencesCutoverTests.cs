using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class ProviderPreferencesCutoverTests
{
    [Fact]
    public void Speech_preferences_roundtrip_without_a_language_model_alias()
    {
        var preferences = new ProviderPreferences("speech-recognition", "speech-synthesis");
        var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("languageModel", json);
        Assert.Equal(preferences, JsonSerializer.Deserialize<ProviderPreferences>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void Retired_language_model_alias_is_rejected_instead_of_silently_ignored()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProviderPreferences>(
            """{"languageModel":"retired-route","speechRecognizer":null,"speechSynthesizer":null}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
