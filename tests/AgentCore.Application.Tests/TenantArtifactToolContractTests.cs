using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class TenantArtifactToolContractTests
{
    [Fact]
    public void Publication_preserves_exact_content_and_canonicalizes_only_metadata_and_field_order()
    {
        var first = TenantArtifactToolContract.Parse("""{"content":"# thử nghiệm\r\nExact ","displayName":"../report.txt"}""");
        var second = TenantArtifactToolContract.Parse("""{"displayName":"report.txt","contentType":"text/markdown","content":"# thử nghiệm\r\nExact "}""");
        Assert.Equal(first, second); Assert.Equal("# thử nghiệm\r\nExact ", first.Content); Assert.Equal("report.txt", first.DisplayName);
        Assert.NotEqual(first.ParametersHash, TenantArtifactToolContract.Parse("""{"displayName":"report.txt","content":"# thử nghiệm\nExact "}""").ParametersHash);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"displayName\":\"report\",\"displayName\":\"other\",\"content\":\"x\"}")]
    [InlineData("{\"displayName\":\"report\",\"content\":\"x\",\"artifactId\":\"user-selected\"}")]
    [InlineData("{\"displayName\":\"report\",\"content\":\"x\",\"sessionId\":\"other\"}")]
    [InlineData("{\"displayName\":\"report\",\"content\":1}")]
    [InlineData("{\"displayName\":\"report\",\"content\":\"x\",\"contentType\":\"bad type\"}")]
    public void Publication_rejects_ambiguous_fields_and_caller_selected_authority(string json) => Assert.Throws<ArgumentException>(() => TenantArtifactToolContract.Parse(json));
    [Fact]
    public void Publication_bounds_UTF8_bytes_independently_of_character_count()
    {
        var value = System.Text.Json.JsonSerializer.Serialize(new { displayName = "report.txt", content = new string('a', TenantArtifactToolContract.MaxContentBytes) });
        Assert.Equal(TenantArtifactToolContract.MaxContentBytes, TenantArtifactToolContract.Parse(value).Content.Length);
        var unicode = "{\"displayName\":\"report.txt\",\"content\":\"" + new string('é', TenantArtifactToolContract.MaxContentBytes / 2) + "\"}";
        var parsed = TenantArtifactToolContract.Parse(unicode);
        Assert.Equal(parsed, TenantArtifactToolContract.Parse(parsed.CanonicalArguments));
        value = System.Text.Json.JsonSerializer.Serialize(new { displayName = "report.txt", content = new string('é', TenantArtifactToolContract.MaxContentBytes / 2 + 1) });
        Assert.Throws<ArgumentException>(() => TenantArtifactToolContract.Parse(value));
    }
}
