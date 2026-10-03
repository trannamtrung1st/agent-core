using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserObserveContractTests
{
    [Fact]
    public void Schema_advertises_only_bounded_stable_wait()
    {
        var descriptor = ToolRegistry.Get(ToolCatalog.BrowserObserve);
        using var schema = JsonDocument.Parse(descriptor.ModelDefinition.ParametersJson);
        var root = schema.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        var properties = root.GetProperty("properties");
        Assert.Equal("stable", properties.GetProperty("waitFor").GetProperty("enum")[0].GetString());
        Assert.Equal(
            new[] { "stable", "navigation", "role" },
            properties.GetProperty("waitFor").GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(100, properties.GetProperty("timeoutMs").GetProperty("minimum").GetInt32());
        Assert.Equal(5000, properties.GetProperty("timeoutMs").GetProperty("maximum").GetInt32());
        Assert.Contains(
            "do not conclude that the data is absent solely from the first observation",
            descriptor.ModelDefinition.Description,
            StringComparison.Ordinal);
        Assert.Contains("bounded wait for stability", descriptor.ModelDefinition.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("nopCommerce", descriptor.ModelDefinition.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("selector", descriptor.ModelDefinition.ParametersJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sleep", descriptor.ModelDefinition.ParametersJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"waitFor":"stable"}""")]
    [InlineData("""{"waitFor":"stable","timeoutMs":100}""")]
    [InlineData("""{"waitFor":"stable","timeoutMs":2500}""")]
    [InlineData("""{"waitFor":"stable","timeoutMs":5000}""")]
    [InlineData("""{"waitFor":"navigation","timeoutMs":1000}""")]
    [InlineData("""{"waitFor":"role","role":"button","name":"Search"}""")]
    public void Supported_observe_arguments_are_accepted(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(BrowserToolArguments.TryObserve(document.RootElement, out var options, out var error), error);
        if (json == "{}")
        {
            Assert.Null(options);
            return;
        }

        Assert.Contains(options!.WaitFor, BrowserToolLimits.ObserveWaitModes);
        if (json.Contains("timeoutMs", StringComparison.Ordinal))
        {
            Assert.InRange(options.TimeoutMs!.Value, 100, 5000);
        }
        else
        {
            Assert.Null(options.TimeoutMs);
        }
    }

    [Theory]
    [InlineData("""{"waitFor":"stable","timeoutMs":5001}""", "timeout_out_of_range")]
    [InlineData("""{"waitFor":"stable","timeoutMs":99}""", "timeout_out_of_range")]
    [InlineData("""{"waitFor":"stable","timeoutMs":100.5}""", "timeout_out_of_range")]
    [InlineData("""{"waitFor":"networkidle"}""", "unsupported_wait")]
    [InlineData("""{"waitFor":"load"}""", "unsupported_wait")]
    [InlineData("""{"timeoutMs":2000}""", "timeout_without_wait")]
    [InlineData("""{"waitFor":"stable","sleep":1000}""", "unsupported_property")]
    [InlineData("""{"note":"extra"}""", "unsupported_property")]
    [InlineData("""{"waitFor":"stable","selector":"#grid"}""", "unsupported_property")]
    public void Unsupported_observe_arguments_are_rejected(string json, string reason)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(BrowserToolArguments.TryObserve(document.RootElement, out _, out var error));
        using var payload = JsonDocument.Parse(error);
        Assert.Equal(reason, payload.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("#grid", error, StringComparison.Ordinal);
    }
}
