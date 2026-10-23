using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class TenantPublicWebReadContractTests
{
    [Theory]
    [InlineData("web.search", "{}")] [InlineData("web.search", "[]")]
    [InlineData("web.search", "{\"query\":\" \"}")]
    [InlineData("web.search", "{\"query\":\"one\",\"query\":\"two\"}")]
    [InlineData("web.search", "{\"query\":\"one\",\"limit\":0}")]
    [InlineData("web.search", "{\"query\":\"one\",\"limit\":11}")]
    [InlineData("web.search", "{\"query\":\"one\",\"limit\":\"5\"}")]
    [InlineData("web.search", "{\"query\":\"one\",\"principalId\":\"owner\"}")]
    [InlineData("web.fetch", "{\"url\":\"file:///etc/passwd\"}")]
    [InlineData("web.fetch", "{\"url\":\"https://user:secret@example.test/a\"}")]
    [InlineData("web.fetch", "{\"url\":\"http://127.0.0.1/a\"}")]
    [InlineData("web.fetch", "{\"url\":\"http://169.254.169.254/a\"}")]
    [InlineData("web.fetch", "{\"url\":\"https://example.test/a\",\"headers\":{\"Cookie\":\"secret\"}}")]
    [InlineData("web.fetch", "{\"url\":\"https://example.test/a\",\"method\":\"POST\"}")]
    public void Public_reads_reject_ambiguous_or_private_requests(string tool, string json) => Assert.Throws<ArgumentException>(() => TenantPublicWebReadContract.Parse(tool, json));

    [Fact]
    public void Canonical_source_identity_binds_query_limit_URL_and_tool()
    {
        var a = TenantPublicWebReadContract.Parse(ToolCatalog.WebSearch, """{"query":"  public form  "}""");
        Assert.Equal(a, TenantPublicWebReadContract.Parse(a.Tool, a.CanonicalArguments));
        Assert.NotEqual(a.Hash, TenantPublicWebReadContract.Parse(a.Tool, """{"query":"public form","limit":2}""").Hash);
        var fetch = TenantPublicWebReadContract.Parse(ToolCatalog.WebFetch, """{"url":"https://example.test/form"}""");
        Assert.Equal(fetch, TenantPublicWebReadContract.Parse(fetch.Tool, fetch.CanonicalArguments));
        Assert.NotEqual(fetch.Hash, TenantPublicWebReadContract.Parse(fetch.Tool, """{"url":"https://example.test/other"}""").Hash);
    }
    [Fact]
    public void Public_read_descriptors_are_closed_and_bounded()
    {
        foreach (var tool in new[] { ToolCatalog.WebSearch, ToolCatalog.WebFetch })
        {
            var descriptor = TenantPublicWebReadContract.ForModel(ToolRegistry.Get(tool).ModelDefinition);
            using var json = JsonDocument.Parse(descriptor.ParametersJson); Assert.False(json.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.Contains("no private", descriptor.Description);
        }
        Assert.Throws<ArgumentException>(() => TenantPublicWebReadContract.Parse(ToolCatalog.WebSearch, JsonSerializer.Serialize(new { query = new string('x', 601) })));
    }
}
