using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserInteractionContractTests
{
    private const string Ref = "el_0123456789abcdefghijkl";

    [Fact]
    public void Advertised_schema_matches_operation_specific_required_fields()
    {
        foreach (var name in new[] { "browser.click", "browser.hover", "browser.drag", "browser.type", "browser.fill_form", "browser.select_option", "browser.press_key", "browser.upload", "browser.fill_credential" })
        {
            using var schema = JsonDocument.Parse(ToolRegistry.Get(name).ModelDefinition.ParametersJson);
            var root = schema.RootElement;
            Assert.Equal("object", root.GetProperty("type").GetString());
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            Assert.True(root.TryGetProperty("required", out _));
            Assert.False(root.TryGetProperty("oneOf", out _));
            Assert.False(root.GetProperty("properties").TryGetProperty("selector", out _));
            Assert.False(root.GetProperty("properties").TryGetProperty("path", out _));
        }
    }

    [Fact]
    public void Runtime_validator_agrees_with_each_operation_branch()
    {
        AssertAccepted("click", $$"""{"operation":"click","ref":"{{Ref}}"}""");
        AssertRejected($$"""{"operation":"fill","ref":"{{Ref}}"}""", "missing_value");
        AssertAccepted("fill", $$"""{"operation":"fill","ref":"{{Ref}}","value":"AC Keyboard"}""");
        AssertAccepted("fill_credential", $$"""{"operation":"fill_credential","ref":"{{Ref}}","credentialRef":"store-admin"}""");
        AssertRejected($$"""{"operation":"fill_credential","ref":"{{Ref}}","credentialRef":"store-admin","value":"raw-secret"}""", "unsupported_property", absent: "raw-secret");
        AssertRejected($$"""{"operation":"select","ref":"{{Ref}}"}""", "missing_value");
        AssertAccepted("select", $$"""{"operation":"select","ref":"{{Ref}}","value":"Published"}""");
        AssertRejected($$"""{"operation":"press","ref":"{{Ref}}"}""", "missing_key");
        AssertAccepted("press", $$"""{"operation":"press","ref":"{{Ref}}","key":"Enter"}""");
        AssertAccepted("check", $$"""{"operation":"check","ref":"{{Ref}}"}""");
        AssertAccepted("uncheck", $$"""{"operation":"uncheck","ref":"{{Ref}}"}""");
        AssertRejected($$"""{"operation":"upload","ref":"{{Ref}}"}""", "missing_artifact_id");
        AssertAccepted("upload", $$"""{"operation":"upload","ref":"{{Ref}}","artifactId":"01a0fddd-23b7-7ee9-9fc0-3ab76e455421"}""");
        AssertAccepted("doubleClick", $$"""{"operation":"doubleClick","ref":"{{Ref}}"}""");
        AssertAccepted("hover", $$"""{"operation":"hover","ref":"{{Ref}}"}""");
        AssertAccepted("scroll", """{"operation":"scroll","direction":"down","delta":400}""");
        AssertAccepted("drag", $$"""{"operation":"drag","ref":"{{Ref}}","targetRef":"{{Ref}}"}""");
    }

    [Fact]
    public void Validator_rejects_extra_properties_selectors_and_paths()
    {
        AssertRejected(
            $$"""{"operation":"click","ref":"{{Ref}}","note":"extra"}""",
            "unsupported_property");
        AssertRejected(
            """{"operation":"click","ref":"#product-name"}""",
            "invalid_ref",
            absent: "#product-name");
        AssertRejected(
            $$"""{"operation":"upload","ref":"{{Ref}}","path":"/tmp/ac-keyboard.png"}""",
            "unsupported_property",
            absent: "/tmp/ac-keyboard.png");
        AssertRejected(
            $$"""{"operation":"fill","ref":"{{Ref}}","value":"ok","selector":".name"}""",
            "unsupported_property",
            absent: ".name");
    }

    private static void AssertBranch(
        JsonElement branches,
        string operation,
        string[] required,
        int? valueMaxLength = null)
    {
        var branch = Assert.Single(
            branches.EnumerateArray(),
            item => item.GetProperty("properties").GetProperty("operation").GetProperty("enum")[0].GetString() == operation);
        Assert.False(branch.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(required, branch.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        if (valueMaxLength is int max)
        {
            Assert.Equal(max, branch.GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32());
        }
    }

    private static void AssertAccepted(string operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(BrowserToolArguments.TryInteraction(document.RootElement, out var actual, out _, out _, out var error), error);
        Assert.Equal(operation, actual);
        Assert.DoesNotContain("reason", error, StringComparison.Ordinal);
    }

    private static void AssertRejected(string json, string reason, string? absent = null)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(BrowserToolArguments.TryInteraction(document.RootElement, out _, out _, out _, out var error));
        using var payload = JsonDocument.Parse(error);
        Assert.Equal(reason, payload.RootElement.GetProperty("reason").GetString());
        if (absent is not null)
        {
            Assert.DoesNotContain(absent, error, StringComparison.Ordinal);
        }
    }
}
