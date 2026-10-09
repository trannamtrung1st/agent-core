using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserSnapshotCompactionTests
{
    [Fact]
    public void New_snapshot_preserves_recent_discovery_refs_and_failures()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Tool, "{\"matches\":[{\"ref\":\"el_old\",\"name\":\"Asset 159\"}]}", Name: "browser.find"),
            new(ModelRole.Tool, "{\"error\":\"invalid_reference\",\"message\":\"Omit an unused scopeRef.\"}", Name: "browser.find"),
            Observation(1, "el_latest")
        };
        BrowserSnapshotCompaction.Compact(messages);
        Assert.Contains("el_old", messages[0].Text);
        Assert.Contains("invalid_reference", messages[1].Text);
        Assert.Contains("Omit an unused scopeRef", messages[1].Text);
        Assert.Contains("el_latest", messages[2].Text);
    }

    [Fact]
    public void Older_discoveries_keep_outcomes_without_unbounded_reference_history()
    {
        var messages = Enumerable.Range(0, 20).Select(i => new ModelMessage(ModelRole.Tool,
            JsonSerializer.Serialize(new { status = "ok", matchCount = 1, matches = new[] { new { @ref = $"el_found_{i}" } } }),
            Name: "browser.find")).ToList();
        var error = "{\"error\":\"not_found\",\"message\":\"No rendered target matches.\"}";
        messages.Insert(0, new(ModelRole.Tool, error, Name: "browser.find"));
        messages.Add(Observation(1, "el_latest"));
        BrowserSnapshotCompaction.Compact(messages);
        BrowserSnapshotCompaction.Compact(messages);
        Assert.Equal(error, messages[0].Text);
        Assert.Equal(8, messages.Count(m => m.Text.Contains("el_found_", StringComparison.Ordinal)));
        using var receipt = JsonDocument.Parse(messages[1].Text);
        Assert.Equal("ok", receipt.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, receipt.RootElement.GetProperty("matchCount").GetInt32());
        Assert.Contains("browser.find", receipt.RootElement.GetProperty("guidance").GetString());
    }
    [Fact]
    public void Repeated_browser_rounds_keep_only_the_latest_full_observations()
    {
        var messages = new List<ModelMessage> { new(ModelRole.User, "Publish the product.") };
        for (var round = 0; round < 8; round++)
        {
            messages.Add(new ModelMessage(
                ModelRole.Assistant,
                string.Empty,
                ToolCalls: [new ModelToolCall($"call-{round}", "browser.snapshot", "{}")]));
            messages.Add(Observation(round, $"el_round_{round}"));
        }

        var before = messages.Sum(message => message.Text.Length);
        BrowserSnapshotCompaction.Compact(messages);
        var after = messages.Sum(message => message.Text.Length);

        Assert.True(after < before / 2);
        Assert.Equal(8, messages.Count(message => message.Role == ModelRole.Tool));
        var observations = messages.Where(message => message.Role == ModelRole.Tool).ToArray();
        Assert.Equal(1, observations.Count(message => message.Text.Contains("\"targets\"", StringComparison.Ordinal)));
        for (var index = 0; index < 7; index++)
        {
            var message = observations[index];
            using var document = JsonDocument.Parse(message.Text);
            Assert.True(document.RootElement.GetProperty("untrustedBrowserContent").GetBoolean());
            Assert.True(document.RootElement.GetProperty("compacted").GetBoolean());
            Assert.False(document.RootElement.TryGetProperty("targets", out _));
            Assert.Contains($"TAIL-{index}", document.RootElement.GetProperty("contentExcerpt").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("el_round_", message.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ref\"", message.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"state\"", message.Text, StringComparison.Ordinal);
        }
        Assert.Contains("el_round_7", observations[7].Text, StringComparison.Ordinal);
        Assert.Contains("\"state\"", observations[7].Text, StringComparison.Ordinal);
        Assert.Contains("\"targets\"", observations[7].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("el_round_6", observations[6].Text, StringComparison.Ordinal);
        Assert.Equal("obs-7", observations[7].ToolCallId);
        Assert.Contains(messages, message => message.ToolCalls?.Any(call => call.Id == "call-7") == true);
        Assert.Equal("browser.snapshot", observations[7].Name);
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
            new(ModelRole.Tool, error, ToolCallId: "err", Name: "browser.click"),
            new(ModelRole.Tool, intervention, ToolCallId: "gate", Name: "browser.navigate"),
            new(ModelRole.Tool, note, ToolCallId: "note", Name: "workspace.read"),
            Observation(1, "el_mid"),
            Observation(2, "el_new"),
            Observation(3, "el_latest")
        };

        BrowserSnapshotCompaction.Compact(messages);

        Assert.DoesNotContain("el_old", messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("el_mid", messages[4].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("el_new", messages[5].Text, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(messages[4].Text).RootElement.TryGetProperty("targets", out _));
        Assert.Equal(error, messages[1].Text);
        Assert.Equal(intervention, messages[2].Text);
        Assert.Equal(note, messages[3].Text);
        var latest = JsonDocument.Parse(messages[6].Text).RootElement;
        Assert.True(latest.TryGetProperty("targets", out var targets));
        Assert.Contains("el_latest", targets[0].GetProperty("ref").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Latest_receipt_per_page_keeps_both_ends_and_older_duplicates_stay_short()
    {
        var messages = new List<ModelMessage>
        {
            Page("http://store.test/orders", "ORDER-EARLY", settled: false),
            Page("http://store.test/orders", "ORDER-LATE", settled: true),
            Page("http://store.test/products", "PRODUCT-GRID", settled: null)
        };

        BrowserSnapshotCompaction.Compact(messages);

        var early = JsonDocument.Parse(messages[0].Text).RootElement;
        var late = JsonDocument.Parse(messages[1].Text).RootElement;
        var products = JsonDocument.Parse(messages[2].Text).RootElement;
        Assert.True(early.GetProperty("compacted").GetBoolean());
        Assert.DoesNotContain("ORDER-EARLY", early.GetProperty("contentExcerpt").GetString(), StringComparison.Ordinal);
        Assert.Equal(BrowserSnapshotCompaction.DuplicateReceiptChars, early.GetProperty("contentExcerpt").GetString()!.Length);
        var lateExcerpt = late.GetProperty("contentExcerpt").GetString();
        Assert.Contains("ORDER-LATE", lateExcerpt, StringComparison.Ordinal);
        Assert.True(lateExcerpt!.Length <= BrowserSnapshotCompaction.PageEvidenceChars);
        Assert.Contains("HEADER", late.GetProperty("contentExcerpt").GetString(), StringComparison.Ordinal);
        Assert.True(late.GetProperty("settled").GetBoolean());
        Assert.False(late.TryGetProperty("targets", out _));
        Assert.True(products.TryGetProperty("targets", out _));
        Assert.Contains("PRODUCT-GRID", products.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    private static ModelMessage Page(string url, string marker, bool? settled)
    {
        var visible = "HEADER-" + new string('h', 2000) + "-" + marker;
        var json = settled is bool settledValue
            ? JsonSerializer.Serialize(new
            {
                untrustedBrowserContent = true,
                url,
                title = "Store",
                content = visible,
                settled = settledValue,
                targets = new[] { new { @ref = "el_aaaaaaaaaaaaaaaaaaaaaa", role = "link", name = "Row" } }
            })
            : JsonSerializer.Serialize(new
            {
                untrustedBrowserContent = true,
                url,
                title = "Store",
                content = visible,
                targets = new[] { new { @ref = "el_aaaaaaaaaaaaaaaaaaaaaa", role = "link", name = "Row" } }
            });
        return new ModelMessage(ModelRole.Tool, json, ToolCallId: marker, Name: "browser.snapshot");
    }

    private static ModelMessage Observation(int round, string reference)
    {
        var targets = Enumerable.Range(0, 40)
            .Select(index => index == 0
                ? (object)new { @ref = reference, role = "textbox", name = "SKU", actions = new[] { "fill" }, state = new { value = $"SKU-{reference}" } }
                : new { @ref = $"el_{round}_{index}", role = "button", name = new string('n', 80), actions = new[] { "click" } })
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            untrustedBrowserContent = true,
            url = $"http://127.0.0.1:5088/Admin/Product/Create?round={round}",
            title = "Create product",
            content = new string('v', 4000) + $"TAIL-{round}",
            truncated = false,
            targets
        });
        return new ModelMessage(ModelRole.Tool, json, ToolCallId: $"obs-{round}", Name: "browser.snapshot");
    }
}
