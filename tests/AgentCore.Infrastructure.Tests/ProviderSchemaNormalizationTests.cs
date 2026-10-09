using System.Text.Json;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Tests;

public sealed class ProviderSchemaNormalizationTests
{
    [Fact]
    public void Nullable_normalization_preserves_an_existing_union_constraint()
    {
        var schema = OpenAiCompatibleToolSchema.Normalize("""
            {"type":["string","null"],"anyOf":[{"const":"allowed"},{"type":"null"}]}
            """);
        using var doc = JsonDocument.Parse(schema!.ToJsonString());
        Assert.Equal("allowed", doc.RootElement.GetProperty("anyOf")[0].GetProperty("const").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("allOf")[0].GetProperty("anyOf").GetArrayLength());
    }

    [Fact]
    public void Nullable_enum_uses_equivalent_branches_and_does_not_admit_extra_values()
    {
        var schema = OpenAiCompatibleToolSchema.Normalize("""
            {"type":"object","properties":{"scope":{"type":["string","null"],"enum":["session","global",null]}},"required":[]}
            """);
        using var doc = JsonDocument.Parse(schema!.ToJsonString());
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("required", out _));
        var scope = root.GetProperty("properties").GetProperty("scope");
        Assert.False(scope.TryGetProperty("type", out _));
        var branches = scope.GetProperty("anyOf");
        Assert.Equal("string", branches[0].GetProperty("type").GetString());
        Assert.Equal(new[] { "session", "global" }, branches[0].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("null", branches[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, branches[1].GetProperty("enum")[0].ValueKind);
    }
}
