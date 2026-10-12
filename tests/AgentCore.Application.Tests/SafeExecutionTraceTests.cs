using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Testing;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class SafeExecutionTraceTests
{
    [Theory]
    [InlineData("{\"error\":\"smtp_rejected\"}", "smtp_rejected", LogLevel.Warning)]
    [InlineData("{\"outcome\":\"indeterminate\",\"error\":null}", "indeterminate", LogLevel.Warning)]
    [InlineData("{\"success\":false}", "failed", LogLevel.Warning)]
    [InlineData("{\"status\":503}", "http_error", LogLevel.Warning)]
    [InlineData("{\"statusCode\":429}", "http_error", LogLevel.Warning)]
    [InlineData("{\"status\":200}", "ok", LogLevel.Information)]
    [InlineData("{\"statusCode\":\"private-body\"}", "ok", LogLevel.Information)]
    [InlineData("{\"status\":\"queued\"}", "queued", LogLevel.Information)]
    [InlineData("{\"status\":\"Running\"}", "Running", LogLevel.Information)]
    [InlineData("{\"status\":\"Failed\"}", "Failed", LogLevel.Information)]
    [InlineData("{\"error\":\"Failed\"}", "error.Failed", LogLevel.Warning)]
    [InlineData("{\"error\":\"ok\"}", "error.ok", LogLevel.Warning)]
    [InlineData("{\"error\":\"account_selection_required\"}", "error.account_selection_required", LogLevel.Warning)]
    [InlineData("{\"outcome\":\"indeterminate\",\"error\":\"cancelled\"}", "indeterminate", LogLevel.Warning)]
    [InlineData("{\"error\":\"password=private-secret\"}", "tool_failed", LogLevel.Warning)]
    public void Tool_failures_are_visible_at_warning_without_logging_payloads(string json, string expected, LogLevel level)
    {
        var logs = new DiagnosticLogCapture<SafeExecutionTraceTests>();
        var outcome = SafeExecutionTrace.NormalizeToolOutcome(json);
        Assert.Equal(expected, outcome);
        SafeExecutionTrace.RecordToolStep(logs, Guid.NewGuid(), Guid.NewGuid(), 1, ToolCatalog.EmailSend, 1, outcome, null);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(level, entry.Level);
        Assert.DoesNotContain("private", entry.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void Non_object_tool_results_do_not_break_runtime_tracing(string result)
    {
        Assert.Equal("ok", SafeExecutionTrace.NormalizeToolOutcome(result));
        Assert.Empty(SafeExecutionTrace.BuildToolDetail("skills.list", "{}", result));
    }

    [Fact]
    public void RecordToolStep_includes_tool_name_and_step_number()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var sessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var responseId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        SafeExecutionTrace.RecordToolStep(
            NullLogger.Instance,
            sessionId,
            responseId,
            2,
            ToolCatalog.BrowserSnapshot,
            12.5,
            "ok",
            "path=http://127.0.0.1:5088/Admin");

        var eventItem = Assert.Single(RuntimeTelemetry.SnapshotTimeline(), item => item.Stage == SafeExecutionTrace.ToolStepStage);
        Assert.Contains("step=2", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("tool=browser.snapshot", eventItem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObserveDetail_records_wait_and_settled_without_page_text()
    {
        var args = """{"waitFor":"stable","timeoutMs":3000}""";
        var result = """
            {
              "untrustedBrowserContent": true,
              "url": "http://127.0.0.1:5088/Admin/Order/List",
              "title": "Orders",
              "visibleText": "Order 1001 Processing",
              "settled": false,
              "elements": [
                {"target":{"by":"role","value":"button","name":"el_secretrefsecretrefsec"},"role":"link","name":"Orders","actions":["click"]}
              ]
            }
            """;

        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserSnapshot, args, result);
        Assert.Contains("feature=Snapshot", detail, StringComparison.Ordinal);
        Assert.Contains("settled=false", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Order 1001", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("el_secretrefsecretrefsec", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Processing", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://private-user:private-password@example.test/customer/private-customer?token=private-token")]
    [InlineData("private-customer")]
    public void Browser_navigation_trace_omits_private_url_components(string url)
    {
        var json = JsonSerializer.Serialize(new { url });
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserNavigate, json, json);
        Assert.DoesNotContain("private-", detail, StringComparison.Ordinal);
        if (url.StartsWith("https:", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("example.test", detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Invalid_browser_arguments_do_not_expose_secret_values_in_error_or_trace()
    {
        const string reference = "el_0123456789abcdefghijkl";
        const string secret = "do-not-log-fill-value";
        var json = System.Text.Json.JsonSerializer.Serialize(new { target = reference, selector = secret });
        using var args = JsonDocument.Parse(json);
        Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserClick, args.RootElement, out _, out var error));
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserClick, json, "{\"error\":\"" + error + "\"}");
        Assert.DoesNotContain(secret, detail); Assert.DoesNotContain(reference, detail);
    }

    [Fact]
    public void BuildActDetail_does_not_record_fill_value_or_ref()
    {
        var args = """{"operation":"fill","target":{"by":"role","value":"button","name":"secret-ref"},"value":"super-secret-value"}""";
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserClick, args, "{}");
        Assert.DoesNotContain("super-secret-value", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ref", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_does_not_record_upload_content_or_path()
    {
        var args = """{"operation":"upload","target":{"by":"role","value":"button","name":"e9"},"artifactId":"11111111-1111-1111-1111-111111111111"}""";
        var result = """
            {
              "url": "http://127.0.0.1:5088/Admin/Product/Edit/48",
              "elements": [
                {"target":{"by":"role","value":"button","name":"e9"},"role":"file","name":"Picture","actions":["upload"]}
              ]
            }
            """;
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserUpload, args, result);
        Assert.Contains("feature=Upload", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("targetName=", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactId", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace/", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_omits_sensitive_accessible_names()
    {
        Assert.True(SafeExecutionTrace.SensitiveAccessibleName("API token field"));
        var args = """{"operation":"fill","target":{"by":"role","value":"button","name":"e3"}}""";
        var result = """
            {
              "elements": [
                {"target":{"by":"role","value":"button","name":"e3"},"role":"textbox","name":"API token","actions":["fill"]}
              ]
            }
            """;
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserClick, args, result);
        Assert.Contains("feature=Click", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("API token", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("targetName=", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordGenerationTerminal_records_structural_fields_without_response_content()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var sessionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var responseId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        SafeExecutionTrace.RecordGenerationTerminal(
            NullLogger.Instance,
            sessionId,
            responseId,
            failed: false,
            channel: "structuredOutput",
            stopReason: "completed",
            disposition: "Complete",
            actionKind: "chat.respond",
            hasDisplayText: true);

        var eventItem = Assert.Single(RuntimeTelemetry.SnapshotTimeline(), item => item.Stage == SafeExecutionTrace.GenerationTerminalStage);
        Assert.Contains("channel=structuredOutput", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("disposition=Complete", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("action=chat.respond", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("hasDisplayText=True", eventItem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("keyboard", eventItem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reasoning", eventItem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordGenerationTerminal_can_diagnose_provider_failure_without_message_body()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);

        SafeExecutionTrace.RecordGenerationTerminal(
            NullLogger.Instance,
            Guid.NewGuid(),
            Guid.NewGuid(),
            failed: true,
            channel: "structuredOutput",
            stopReason: "none",
            disposition: "none",
            actionKind: "none",
            hasDisplayText: false,
            failureCategory: "provider",
            failureCode: "InvalidRequest");

        var eventItem = Assert.Single(RuntimeTelemetry.SnapshotTimeline(), item => item.Stage == SafeExecutionTrace.GenerationTerminalStage);
        Assert.Contains("kind=provider_failure", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("failureCategory=provider", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("failureCode=InvalidRequest", eventItem.Detail, StringComparison.Ordinal);
    }
}
