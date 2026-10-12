using System.Net;
using System.Net.Sockets;
using AgentCore.Application.Observability;
using AgentCore.Application.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace AgentCore.Application.Tests;

public sealed class HandledFailureLogTests
{
    [Fact]
    public void Disabled_warnings_preserve_trace_failure_without_consuming_ids_or_snapshotting()
    {
        var id = Guid.NewGuid();
        var diagnostics = new QueueDiagnosticIdSource([id]);
        using var listener = DiagnosticActivity.Listen();
        using var activity = RuntimeTelemetry.Activity.StartActivity("disabled-warning");
        Assert.NotNull(activity);
        Assert.Null(HandledFailureLog.Warning(NullLogger.Instance, new UnsnapshotableException(), "email.read", "mail_read_failed", diagnostics: diagnostics));
        Assert.Null(HandledFailureLog.Warning(null, new UnsnapshotableException(), "email.read", "mail_read_failed", diagnostics: diagnostics));
        Assert.Equal(id, diagnostics.NewId());
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("mail_read_failed", activity.GetTagItem("error.code"));
    }

    [Fact]
    public void Explicit_id_or_injected_source_controls_the_log_reference()
    {
        var first = Guid.NewGuid();
        var supplied = Guid.NewGuid();
        var diagnostics = new QueueDiagnosticIdSource([first]);
        var logs = new DiagnosticLogCapture<HandledFailureLogTests>();
        Assert.Equal(supplied, HandledFailureLog.Warning(logs, new IOException(), "email.read", "mail_read_failed", diagnosticId: supplied, diagnostics: diagnostics));
        Assert.Equal(first, HandledFailureLog.Warning(logs, new IOException(), "email.read", "mail_read_failed", diagnostics: diagnostics));
        Assert.Equal(supplied, logs.Entries[0].Properties["DiagnosticId"]);
        Assert.Equal(first, logs.Entries[1].Properties["DiagnosticId"]);
    }

    private sealed class UnsnapshotableException : Exception
    {
        public override string? StackTrace => throw new InvalidOperationException("Disabled logging must not inspect exception stacks.");
    }

    [Fact]
    public void Aggregate_failures_retain_bounded_sibling_causes_without_their_messages()
    {
        var logs = new DiagnosticLogCapture<HandledFailureLogTests>();
        var failure = new AggregateException("private top-level content",
            new IOException("private first cause"), new SocketException((int)SocketError.ConnectionRefused));
        HandledFailureLog.Warning(logs, failure, "workspace.cleanup", "cleanup_failed");
        var snapshot = Assert.IsAssignableFrom<AggregateException>(Assert.Single(logs.Entries).Exception);
        Assert.Equal(2, snapshot.InnerExceptions.Count);
        Assert.Contains(nameof(IOException), snapshot.ToString());
        Assert.Contains("SocketErrorCode=ConnectionRefused", snapshot.ToString());
        Assert.DoesNotContain("private", snapshot.ToString());

        HandledFailureLog.Warning(logs, new AggregateException(Enumerable.Range(0, 20).Select(_ => new IOException("private"))), "workspace.cleanup", "cleanup_failed");
        Assert.Equal(8, Assert.IsAssignableFrom<AggregateException>(logs.Entries[1].Exception).InnerExceptions.Count);

        Exception branching = new IOException("private leaf");
        for (var depth = 0; depth < 8; depth++)
            branching = new AggregateException(Enumerable.Repeat(branching, 8));
        HandledFailureLog.Warning(logs, branching, "workspace.cleanup", "cleanup_failed");
        Assert.InRange(CountNodes(logs.Entries[2].Exception!), 1, 32);
    }

    private static int CountNodes(Exception exception) => 1 + (exception is AggregateException aggregate
        ? aggregate.InnerExceptions.Sum(CountNodes)
        : exception.InnerException is { } inner ? CountNodes(inner) : 0);

    [Fact]
    public void Exception_rendering_retains_cause_codes_and_stack_without_messages_or_data()
    {
        var logs = new DiagnosticLogCapture<HandledFailureLogTests>();
        var failure = CaptureFailure();
        failure.Data["Authorization"] = "secret-data";
        var id = HandledFailureLog.Warning(logs, failure, "http.request", "transport_error");
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(id, entry.Properties["DiagnosticId"]);
        Assert.Equal("http.request", entry.Properties["Operation"]);
        var rendered = entry.Exception!.ToString();
        Assert.Contains(nameof(HttpRequestException), rendered);
        Assert.Contains(nameof(SocketException), rendered);
        Assert.Contains("StatusCode=503", rendered);
        Assert.Contains("SocketErrorCode=ConnectionRefused", rendered);
        Assert.Contains(nameof(CaptureFailure), rendered);
        Assert.DoesNotContain("private", rendered);
        Assert.DoesNotContain("secret", rendered);
        Assert.Empty(entry.Exception.Data);
    }

    private static Exception CaptureFailure()
    {
        try { throw new HttpRequestException("https://user:secret@example.test/private?token=secret", new SocketException((int)SocketError.ConnectionRefused), HttpStatusCode.ServiceUnavailable); }
        catch (Exception ex) { return ex; }
    }
}
