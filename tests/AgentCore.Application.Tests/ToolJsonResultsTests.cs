using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ToolJsonResultsTests
{
    [Fact]
    public void FitToBudget_returns_original_json_when_within_budget()
    {
        const string json = """{"ok":true}""";
        Assert.Equal(json, ToolJsonResults.FitToBudget(32, json));
    }

    [Fact]
    public void FitToBudget_returns_truncated_fallback_when_budget_fits_envelope()
    {
        const string json = """{"content":"this payload is far too large for the remaining budget"}""";
        var result = ToolJsonResults.FitToBudget(43, json);
        Assert.Equal("""{"truncated":true,"error":"output_limit"}""", result);
        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void FitToBudget_returns_minimal_valid_json_when_budget_is_below_fallback()
    {
        var result = ToolJsonResults.FitToBudget(2, """{"content":"too large"}""");
        Assert.Equal("{}", result);
        JsonDocument.Parse(result);
    }

    [Fact]
    public void FitToBudget_returns_empty_string_when_no_valid_json_fits()
    {
        Assert.Equal(string.Empty, ToolJsonResults.FitToBudget(0, """{"content":"too large"}"""));
        Assert.Equal(string.Empty, ToolJsonResults.FitToBudget(1, """{"content":"too large"}"""));
    }

    [Fact]
    public void FitToBudget_never_returns_malformed_truncated_fallback()
    {
        const string fallback = """{"truncated":true,"error":"output_limit"}""";
        var fallbackBytes = Encoding.UTF8.GetByteCount(fallback);
        for (var budget = 0; budget <= fallbackBytes; budget++)
        {
            var result = ToolJsonResults.FitToBudget(budget, """{"content":"too large"}""");
            if (result.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(result);
            Assert.True(document.RootElement.ValueKind is JsonValueKind.Object);
        }
    }
}
