using System.Diagnostics;
using AgentCore.Application.Observability;
using AgentCore.Application.Testing;

namespace AgentCore.Application.Tests;

public sealed class DiagnosticLogTests
{
    private static readonly Guid DiagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
    private static readonly Guid CorrelationId = Guid.Parse("019944af-00d7-7000-8000-0000000000c1");
    private static readonly Guid SessionId = Guid.Parse("019944af-00d7-7000-8000-0000000000a1");

    [Fact]
    public void Diagnosed_failure_logs_the_exception_and_allowlisted_fields_only()
    {
        var failure = new InvalidOperationException("api_key=log-secret");
        var logs = new DiagnosticLogCapture<DiagnosticLogTests>();
        using var listener = DiagnosticActivity.Listen();
        using var activity = RuntimeTelemetry.Activity.StartActivity("diagnostic-log");
        Assert.NotNull(activity);

        DiagnosticLog.Warning(
            logs,
            failure,
            DiagnosticId,
            "Model execution failed.",
            new DiagnosticContext(
                CorrelationId: CorrelationId,
                SessionId: SessionId,
                ErrorCategory: "provider",
                ErrorCode: "unavailable",
                ProviderAlias: "bad\nalias"));

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(DiagnosticId, entry.Properties["DiagnosticId"]);
        Assert.Equal(DiagnosticId, entry.Scope["DiagnosticId"]);
        Assert.Equal(CorrelationId, entry.Scope["CorrelationId"]);
        Assert.Equal(SessionId, entry.Scope["SessionId"]);
        Assert.Equal(activity.TraceId.ToHexString(), entry.Scope["TraceId"]);
        Assert.Equal("provider", entry.Scope["ErrorCategory"]);
        Assert.Equal("unavailable", entry.Scope["ErrorCode"]);
        Assert.False(entry.Scope.ContainsKey("ProviderAlias"));
        Assert.False(entry.Scope.ContainsKey("ResponseId"));
        Assert.DoesNotContain("api_key", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("log-secret", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Model execution failed.", entry.Message, StringComparison.Ordinal);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("provider", activity.GetTagItem("error.category"));
        Assert.Equal("unavailable", activity.GetTagItem("error.code"));
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key is "DiagnosticId" or "TraceId" or "SessionId");
    }

    [Fact]
    public void Unsafe_category_is_omitted_from_the_activity_and_scope()
    {
        var logs = new DiagnosticLogCapture<DiagnosticLogTests>();
        using var listener = DiagnosticActivity.Listen();
        using var activity = RuntimeTelemetry.Activity.StartActivity("diagnostic-log-unsafe");
        Assert.NotNull(activity);

        DiagnosticLog.Warning(
            logs,
            new InvalidOperationException("stack-like detail"),
            DiagnosticId,
            "Execution failed.",
            new DiagnosticContext(ErrorCategory: "{\"injected\":true}", ErrorCode: "a/b"));

        var entry = Assert.Single(logs.Entries);
        Assert.False(entry.Scope.ContainsKey("ErrorCategory"));
        Assert.False(entry.Scope.ContainsKey("ErrorCode"));
        Assert.Null(activity.GetTagItem("error.category"));
        Assert.Null(activity.GetTagItem("error.code"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.DoesNotContain("injected", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stack-like", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Absent_activity_does_not_invent_a_trace_id()
    {
        var logs = new DiagnosticLogCapture<DiagnosticLogTests>();
        DiagnosticLog.Warning(
            logs,
            new InvalidOperationException("hidden"),
            DiagnosticId,
            "Execution failed.");

        var entry = Assert.Single(logs.Entries);
        Assert.False(entry.Scope.ContainsKey("TraceId"));
        Assert.DoesNotContain("hidden", entry.Message, StringComparison.Ordinal);
    }
}
