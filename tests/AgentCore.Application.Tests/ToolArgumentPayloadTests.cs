using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ToolArgumentPayloadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Absent_payload_normalizes_only_for_offered_parameterless_defaults(string? payload)
    {
        Assert.Equal("{}", ToolArgumentPayload.Normalize(payload, ToolRegistry.Get(ToolCatalog.BrowserClose).ModelDefinition));
        Assert.Equal("{}", ToolArgumentPayload.Normalize(payload, ToolRegistry.Get("browser.get_config").ModelDefinition));
        Assert.Equal(payload ?? "", ToolArgumentPayload.Normalize(payload, new("fixture", "", ToolRegistry.Get(ToolCatalog.BrowserClose).ModelDefinition.ParametersJson)));
        Assert.Equal(payload ?? "", ToolArgumentPayload.Normalize(payload, ToolRegistry.Get(ToolCatalog.BrowserSnapshot).ModelDefinition));
        Assert.Equal(payload ?? "", ToolArgumentPayload.Normalize(payload, null));
        foreach (var schema in new[] { "{}", "{\"type\":\"object\",\"properties\":{}}", "{\"type\":\"array\"}", "invalid",
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{},\"required\":[\"missing\"]}",
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{},\"patternProperties\":{\".*\":{}}}",
            """{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["close","reset"]}}}""",
            """{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["close"]}},"required":["operation"]}""" })
        {
            Assert.Equal(payload ?? "", ToolArgumentPayload.Normalize(payload, new("fixture", "", schema)));
            Assert.Equal(payload ?? "", ToolArgumentPayload.Normalize(payload, new(ToolCatalog.BrowserClose, "", schema)));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{broken")]
    [InlineData("\"\"")]
    [InlineData(" {} ")]
    [InlineData("""{"operation":"close"}""")]
    public void Explicit_payloads_are_preserved_for_strict_validation(string payload) =>
        Assert.Equal(payload, ToolArgumentPayload.Normalize(payload, ToolRegistry.Get(ToolCatalog.BrowserClose).ModelDefinition));
}
