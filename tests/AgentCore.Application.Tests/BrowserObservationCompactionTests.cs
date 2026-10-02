using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserObservationCompactionTests
{
    [Fact]
    public void Repeated_browser_rounds_keep_only_the_latest_full_observations()
    {
        var messages = new List<ModelMessage> { new(ModelRole.User, "Publish the product.") };
        for (var round = 0; round < 8; round++)
        {
            messages.Add(new ModelMessage(
                ModelRole.Assistant,
                string.Empty,
                ToolCalls: [new ModelToolCall($"call-{round}", "browser.observe", "{}")]));
            messages.Add(Observation(round, $"el_round_{round}"));
        }

        var before = messages.Sum(message => message.Text.Length);
        BrowserObservationCompaction.Compact(messages);
        var after = messages.Sum(message => message.Text.Length);

        Assert.True(after < before / 2);
        Assert.Equal(8, messages.Count(message => message.Role == ModelRole.Tool));
        var observations = messages.Where(message => message.Role == ModelRole.Tool).ToArray();
        Assert.Equal(1, observations.Count(message => message.Text.Contains("\"elements\"", StringComparison.Ordinal)));
        Assert.All(observations.Take(7), message =>
        {
            using var document = JsonDocument.Parse(message.Text);
            Assert.True(document.RootElement.GetProperty("untrustedBrowserContent").GetBoolean());
            Assert.True(document.RootElement.GetProperty("compacted").GetBoolean());
            Assert.False(document.RootElement.TryGetProperty("elements", out _));
            Assert.DoesNotContain("el_round_", message.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ref\"", message.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"state\"", message.Text, StringComparison.Ordinal);
        });
        Assert.Contains("el_round_7", observations[7].Text, StringComparison.Ordinal);
        Assert.Contains("\"state\"", observations[7].Text, StringComparison.Ordinal);
        Assert.Contains("\"elements\"", observations[7].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("el_round_6", observations[6].Text, StringComparison.Ordinal);
        Assert.Equal("obs-7", observations[7].ToolCallId);
        Assert.Contains(messages, message => message.ToolCalls?.Any(call => call.Id == "call-7") == true);
        Assert.Equal("browser.observe", observations[7].Name);
    }

    [Fact]
    public void Browser_errors_and_non_browser_results_stay_intact()
    {
        var error = """{"error":"timeout","message":"Browser operation timed out."}""";
        var intervention = """{"error":"user_intervention_required","kind":"authentication","url":"http://127.0.0.1:5088/login","title":"Sign in","message":"Complete this step in the browser, then tell me to continue."}""";
        var note = """{"path":"/workspace/working/note.txt","content":"keep me"}""";
        var messages = new List<ModelMessage>
        {
            Observation(0, "el_old"),
            new(ModelRole.Tool, error, ToolCallId: "err", Name: "browser.act"),
            new(ModelRole.Tool, intervention, ToolCallId: "gate", Name: "browser.navigate"),
            new(ModelRole.Tool, note, ToolCallId: "note", Name: "workspace.read"),
            Observation(1, "el_mid"),
            Observation(2, "el_new"),
            Observation(3, "el_latest")
        };

        BrowserObservationCompaction.Compact(messages);

        Assert.DoesNotContain("el_old", messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("el_mid", messages[4].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("el_new", messages[5].Text, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(messages[4].Text).RootElement.TryGetProperty("elements", out _));
        Assert.Equal(error, messages[1].Text);
        Assert.Equal(intervention, messages[2].Text);
        Assert.Equal(note, messages[3].Text);
        var latest = JsonDocument.Parse(messages[6].Text).RootElement;
        Assert.True(latest.TryGetProperty("elements", out var elements));
        Assert.Contains("el_latest", elements[0].GetProperty("ref").GetString(), StringComparison.Ordinal);
    }

    private static ModelMessage Observation(int round, string reference)
    {
        var elements = Enumerable.Range(0, 40)
            .Select(index => index == 0
                ? (object)new { @ref = reference, role = "textbox", name = "SKU", actions = new[] { "fill" }, state = new { value = $"SKU-{reference}" } }
                : new { @ref = $"el_{round}_{index}", role = "button", name = new string('n', 80), actions = new[] { "click" } })
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            untrustedBrowserContent = true,
            url = $"http://127.0.0.1:5088/Admin/Product/Create?round={round}",
            title = "Create product",
            visibleText = new string('v', 4000) + reference,
            textTruncated = false,
            elements
        });
        return new ModelMessage(ModelRole.Tool, json, ToolCallId: $"obs-{round}", Name: "browser.observe");
    }
}
