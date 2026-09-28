using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class KnowledgeSourcePathTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void ResolveBackingPath_uses_the_legacy_convention_when_resource_path_is_absent()
    {
        var source = new KnowledgeSourceRef("refund-policy", "Refund Policy", "refund-policy@v3");

        Assert.Equal("knowledge/refund-policy", KnowledgeSourcePaths.ResolveBackingPath(source));
    }

    [Fact]
    public void ResolveBackingPath_uses_an_explicit_path()
    {
        var source = new KnowledgeSourceRef(
            "refund-policy",
            "Refund Policy",
            "refund-policy@v3",
            " knowledge/refund-policy.md ");

        Assert.Equal("knowledge/refund-policy.md", KnowledgeSourcePaths.ResolveBackingPath(source));
    }

    [Fact]
    public void ResourcePath_is_omitted_when_null_and_round_trips_when_present()
    {
        var legacy = JsonSerializer.Serialize(
            new KnowledgeSourceRef("policy", "Policy", "policy@demo"),
            Json);
        Assert.DoesNotContain("resourcePath", legacy, StringComparison.Ordinal);
        var readLegacy = JsonSerializer.Deserialize<KnowledgeSourceRef>(legacy, Json);
        Assert.Null(readLegacy!.ResourcePath);

        var explicitSource = new KnowledgeSourceRef("policy", "Policy", "policy@demo", "knowledge/policy.md");
        var stored = JsonSerializer.Serialize(explicitSource, Json);
        Assert.Contains("\"resourcePath\":\"knowledge/policy.md\"", stored, StringComparison.Ordinal);
        var readExplicit = JsonSerializer.Deserialize<KnowledgeSourceRef>(stored, Json);
        Assert.Equal("knowledge/policy.md", readExplicit!.ResourcePath);
    }
}
