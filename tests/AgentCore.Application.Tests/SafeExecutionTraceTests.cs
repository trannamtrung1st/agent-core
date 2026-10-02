using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class SafeExecutionTraceTests
{
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
            ToolCatalog.BrowserObserve,
            12.5,
            "ok",
            "path=http://127.0.0.1:5088/Admin");

        var eventItem = Assert.Single(RuntimeTelemetry.SnapshotTimeline(), item => item.Stage == SafeExecutionTrace.ToolStepStage);
        Assert.Contains("step=2", eventItem.Detail, StringComparison.Ordinal);
        Assert.Contains("tool=browser.observe", eventItem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_records_operation_and_safe_target_metadata()
    {
        var args = """{"operation":"fill","ref":"e12","value":"AC Keyboard"}""";
        var result = """
            {
              "untrustedBrowserContent": true,
              "url": "http://127.0.0.1:5088/Admin/Product/Create",
              "elements": [
                {"ref":"e12","role":"textbox","name":"Product name","actions":["fill"]}
              ]
            }
            """;

        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserAct, args, result);
        Assert.Contains("operation=fill", detail, StringComparison.Ordinal);
        Assert.Contains("targetRole=textbox", detail, StringComparison.Ordinal);
        Assert.Contains("targetName=Product name", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("AC Keyboard", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("e12", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_does_not_record_fill_value_or_ref()
    {
        var args = """{"operation":"fill","ref":"secret-ref","value":"super-secret-value"}""";
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserAct, args, "{}");
        Assert.DoesNotContain("super-secret-value", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ref", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_does_not_record_upload_content_or_path()
    {
        var args = """{"operation":"upload","ref":"e9","artifactId":"11111111-1111-1111-1111-111111111111"}""";
        var result = """
            {
              "url": "http://127.0.0.1:5088/Admin/Product/Edit/48",
              "elements": [
                {"ref":"e9","role":"file","name":"Picture","actions":["upload"]}
              ]
            }
            """;
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserAct, args, result);
        Assert.Contains("operation=upload", detail, StringComparison.Ordinal);
        Assert.Contains("targetName=Picture", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactId", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace/", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_omits_sensitive_accessible_names()
    {
        Assert.True(SafeExecutionTrace.SensitiveAccessibleName("API token field"));
        var args = """{"operation":"fill","ref":"e3"}""";
        var result = """
            {
              "elements": [
                {"ref":"e3","role":"textbox","name":"API token","actions":["fill"]}
              ]
            }
            """;
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserAct, args, result);
        Assert.Contains("targetRole=textbox", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("API token", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("targetName=", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildActDetail_records_closed_argument_reason_without_value_ref_or_artifact()
    {
        const string reference = "el_0123456789abcdefghijkl";
        const string secret = "do-not-log-fill-value";
        var missing = $$"""{"operation":"fill","ref":"{{reference}}","value":"{{secret}}"}""";
        using var missingDocument = JsonDocument.Parse($$"""{"operation":"fill","ref":"{{reference}}"}""");
        Assert.False(BrowserToolArguments.TryAct(
            missingDocument.RootElement,
            out _,
            out _,
            out _,
            out var error));
        var detail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserAct, missing, error);
        Assert.Contains("operation=fill", detail, StringComparison.Ordinal);
        Assert.Contains("argumentReason=missing_value", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(reference, detail, StringComparison.Ordinal);

        var uploadArgs = $$"""{"operation":"upload","ref":"{{reference}}","artifactId":"/tmp/ac-keyboard.png"}""";
        using var upload = JsonDocument.Parse(uploadArgs);
        Assert.False(BrowserToolArguments.TryAct(upload.RootElement, out _, out _, out _, out var uploadError));
        var uploadDetail = SafeExecutionTrace.BuildToolDetail(ToolCatalog.BrowserAct, uploadArgs, uploadError);
        Assert.Contains("argumentReason=invalid_artifact_id", uploadDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/ac-keyboard.png", uploadDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(reference, uploadDetail, StringComparison.Ordinal);
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
