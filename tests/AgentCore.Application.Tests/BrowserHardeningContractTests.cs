using System.Net;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserHardeningContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{\"text\":\"   \"}")]
    public void Text_verification_is_rejected_with_or_without_a_target(string extra)
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extra)!;
        json["condition"] = JsonSerializer.SerializeToElement("text");
        foreach (var targeted in new[] { false, true })
        {
            if (targeted) json["target"] = JsonSerializer.SerializeToElement(new { by = "role", value = "heading", name = "Record" });
            Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), "browser.verify", JsonSerializer.SerializeToElement(json), out _, out var error));
            Assert.Equal("invalid", error);
        }
    }

    [Fact]
    public void Meaningful_text_and_empty_field_value_are_valid_and_schema_serializes_consistently()
    {
        foreach (var json in new[] { """{"condition":"text","text":"approved"}""", """{"condition":"value","value":"","target":{"by":"label","value":"Notes"}}""" })
            Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), "browser.verify", JsonSerializer.Deserialize<JsonElement>(json), out _, out _));
        Assert.False(BrowserToolArguments.ValidVerification(new("value", new("label", "Notes"))));
        Assert.False(BrowserToolArguments.ValidVerification(new("unknown", new("label", "Notes"))));
        Assert.True(BrowserToolCatalog.TryGet("browser.verify", out var metadata));
        using var schema = JsonDocument.Parse(metadata.ParametersJson);
        Assert.Equal(1, schema.RootElement.GetProperty("properties").GetProperty("text").GetProperty("minLength").GetInt32());
        Assert.Contains("non-whitespace", JsonSerializer.Serialize(metadata.Descriptor), StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_preserves_failure_and_uncertainty_evidence()
    {
        var original = JsonSerializer.Serialize(new { error = "timeout", message = new string('x', 2000),
            effectAttempted = true, effectConfirmedBySdk = false, applicationOutcomeVerified = false });
        using var clipped = JsonDocument.Parse(ToolJsonResults.FitToBudget(200, original));
        Assert.Equal("timeout", clipped.RootElement.GetProperty("error").GetString());
        Assert.True(clipped.RootElement.GetProperty("effectAttempted").GetBoolean());
        Assert.False(clipped.RootElement.GetProperty("effectConfirmedBySdk").GetBoolean());
        Assert.False(clipped.RootElement.GetProperty("applicationOutcomeVerified").GetBoolean());
    }

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("100.100.100.200", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    public void Destination_policy_blocks_metadata_and_link_local_but_preserves_enterprise_and_fixture_IPs(string ip, bool forbidden)
    {
        Assert.Equal(forbidden, BrowserTargetPolicy.IsForbiddenAddress(IPAddress.Parse(ip)));
        var url = new UriBuilder("http", ip).Uri.AbsoluteUri;
        Assert.Equal(!forbidden, BrowserTargetPolicy.EvaluateDestination(url, [url], BrowserPolicyMode.Restricted).Allowed);
        Assert.Equal(!forbidden, BrowserTargetPolicy.EvaluateDestination(url, [], BrowserPolicyMode.OpenWeb).Allowed);
    }

    [Fact]
    public void Operational_budgets_validate_absolute_caps_and_do_not_change_previous_pins()
    {
        BrowserOperationalLimits.Default.Validate();
        var original = new BrowserOperationalLimits(); var lowered = original with { SnapshotBytes = 512, DownloadBytes = 1024, CapturesPerScope = 1, OperationTimeoutMs = 500 };
        lowered.Validate(); Assert.Equal(8000, original.SnapshotBytes);
        foreach (var invalid in new[] { original with { OperationTimeoutMs = 30001 }, original with { DownloadBytes = BrowserToolLimits.MaxDownloadBytes + 1 },
            original with { CapturesPerScope = 0 }, original with { TextInputLength = 501 }, original with { WaitTimeoutMs = 5001 } })
            Assert.Throws<ArgumentException>(() => invalid.Validate());
    }
}
